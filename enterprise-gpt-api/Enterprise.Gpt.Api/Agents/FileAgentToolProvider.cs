using Andes.Extensions.AI;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Settings;
using FluentValidation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Agents;

/// <summary>
/// Composes the File Agent for one turn and hands it to <c>ConversationService</c> as a single
/// tracked tool.
/// </summary>
/// <remarks>
/// <para>
/// <strong>How an artifact gets out of a run.</strong> <c>WithTracking</c> exposes the agent as a
/// string-in/string-out function, so the run's own response — where a produced file's identity
/// appears — is not visible to the caller. Of the three ways to reach it, this uses <em>agent-level
/// middleware</em>: <see cref="AIAgentBuilder.Use(Func{IEnumerable{ChatMessage}, AgentSession,
/// AgentRunOptions, AIAgent, CancellationToken, Task{AgentResponse}}, Func{IEnumerable{ChatMessage},
/// AgentSession, AgentRunOptions, AIAgent, CancellationToken, IAsyncEnumerable{AgentResponseUpdate}})"/>
/// wraps the run, so the same seam that mounts the inputs also sees the response that names the
/// outputs. The alternatives lost for concrete reasons: an agent-scoped "save this file" tool depends
/// on the model remembering to call it, and <c>ChatClientAgentRunOptions.ChatClientFactory</c> is
/// never applied on this path, because <c>AsAIFunction</c> constructs a base <c>AgentRunOptions</c>
/// and only the derived type carries a factory.
/// </para>
/// <para>
/// <strong>Why the agent's client is its own.</strong> It is the same Responses route, but with its
/// own function-invocation bounds — those are instance settings, with no per-request override — and
/// deliberately without tool tracking. A nested tracker opens its own root scope, which would swallow
/// every progress line the run reports and put its activities out of reach of the turn's stream.
/// Leaving it off keeps the ambient scope pointing at the enclosing <c>file_agent</c> tool, and is
/// also why <c>trackUsage</c> below is <see langword="true"/> rather than <see langword="false"/>.
/// </para>
/// </remarks>
// The hosted-file client is still marked experimental in Microsoft.Extensions.AI 10.9.0. Suppressed
// on this one file, as in FileAgentSandbox, rather than project-wide.
#pragma warning disable MEAI001
public sealed class FileAgentToolProvider(
    ILoggerFactory loggerFactory,
    IFileAgentModelResolver modelResolver,
    IFileAgentDocumentReader documentReader,
    IGeneratedDocumentService generatedDocuments,
    IHostedFileClient hostedFiles,
    IServiceProvider services,
    IOptions<FileAgentOptions> options) : IFileAgentToolProvider
{
    /// <summary>The agent's display name, which the activity card renders beside its kind badge.</summary>
    private const string AgentName = "File Agent";

    private const string ToolDescription =
        "Produces a real file — docx, xlsx, pptx, pdf, csv, md or txt — by writing and running Python in a "
        + "sandbox. Use it to create a document from a description, edit or convert a file already in the "
        + "conversation, or compare two of them. Pass one instruction in plain language, naming the files to "
        + "work from exactly as they appear in the conversation. Returns a sentence describing what it made; "
        + "the file itself is attached to your answer automatically.";

    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IFileAgentModelResolver _modelResolver = modelResolver;
    private readonly IFileAgentDocumentReader _documentReader = documentReader;
    private readonly IGeneratedDocumentService _generatedDocuments = generatedDocuments;
    private readonly IHostedFileClient _hostedFiles = hostedFiles;
    private readonly IServiceProvider _services = services;
    private readonly FileAgentOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<IFileAgentToolLease> AcquireAsync(
        Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var model = await _modelResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var chatClient = _services.GetRequiredKeyedService<IChatClient>(ChatClientKeys.FileAgent);
        var fileNames = await _documentReader
            .ListNamesAsync(conversationId, userId, cancellationToken)
            .ConfigureAwait(false);

        var logger = _loggerFactory.CreateLogger<FileAgentToolProvider>();

        // The skills filter needs the run's instruction, which does not exist until the model writes
        // one — so the provider reads it through this rather than being handed a string now.
        var instruction = new RunInstruction();

        var lease = new FileAgentToolLease(
            new FileAgentSandbox(_hostedFiles, logger),
            FileAgentSkills.CreateProvider(FileAgentSkills.DeployedRoot, () => instruction.Text, _loggerFactory));

        try
        {
            var run = new FileAgentRun(
                lease,
                instruction,
                _documentReader,
                _generatedDocuments,
                _options,
                conversationId,
                userId,
                logger);

            // The tool instance is per turn and shared across the run's calls. Mutating its Inputs is
            // safe only because AllowConcurrentInvocation is false on both clients, so at most one
            // call is in flight at a time.
            var codeInterpreter = new HostedCodeInterpreterTool { Inputs = run.Inputs };

            var agent = new ChatClientAgent(
                chatClient,
                new ChatClientAgentOptions
                {
                    Name = AgentName,
                    Description = ToolDescription,
                    ChatOptions = new ChatOptions
                    {
                        ModelId = model.DeploymentName,
                        Instructions = ConversationPrompts.BuildFileAgentPrompt(fileNames),
                        Tools = [codeInterpreter]
                    },
                    AIContextProviders = [lease.Skills],

                    // The client handed in is already a full pipeline. Left false, the agent's own
                    // middleware would write this turn's tools onto that shared client's function
                    // invoker, where the next turn would inherit them.
                    UseProvidedChatClientAsIs = true
                },
                _loggerFactory);

            lease.Tool = agent
                .AsBuilder()
                .Use(run.RunAsync, runStreamingFunc: null)
                .Build()
                .WithTracking(
                    new AIFunctionFactoryOptions
                    {
                        Name = FileAgentToolNames.Agent,
                        Description = ToolDescription,

                        // Runs inside the tracked scope, after the middleware above has stored
                        // whatever the run produced, so what the assistant reads back is the outcome
                        // rather than only the agent's own prose.
                        MarshalResult = (result, _, _) => new ValueTask<object?>(run.Describe(result))
                    },

                    // True because this agent's client carries no tool tracking of its own. With one,
                    // the nested pipeline would roll its usage up on its own and reporting it here
                    // would double every agent token.
                    trackUsage: true);

            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// One turn's borrowed agent, its sandbox uploads and its skill provider.
    /// </summary>
    private sealed class FileAgentToolLease(FileAgentSandbox sandbox, AgentSkillsProvider skills) : IFileAgentToolLease
    {
        private int _disposed;

        public FileAgentSandbox Sandbox { get; } = sandbox;

        public AgentSkillsProvider Skills { get; } = skills;

        public AITool Tool { get; set; } = null!;

        public List<MessageAttachmentDto> Produced { get; } = [];

        /// <inheritdoc />
        public IReadOnlyList<MessageAttachmentDto> GeneratedDocuments => Produced;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await Sandbox.ReleaseAsync().ConfigureAwait(false);
            Skills.Dispose();
        }
    }

    /// <summary>The instruction the model wrote for this run, once it has written one.</summary>
    private sealed class RunInstruction
    {
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// The middleware around one agent run: mounts the files its instruction names, then stores the
    /// files it produced.
    /// </summary>
    private sealed class FileAgentRun(
        FileAgentToolLease lease,
        RunInstruction instruction,
        IFileAgentDocumentReader documentReader,
        IGeneratedDocumentService generatedDocuments,
        FileAgentOptions options,
        Guid conversationId,
        Guid userId,
        ILogger logger)
    {
        private readonly FileAgentToolLease _lease = lease;
        private readonly RunInstruction _instruction = instruction;
        private readonly IFileAgentDocumentReader _documentReader = documentReader;
        private readonly IGeneratedDocumentService _generatedDocuments = generatedDocuments;
        private readonly FileAgentOptions _options = options;
        private readonly Guid _conversationId = conversationId;
        private readonly Guid _userId = userId;
        private readonly ILogger _logger = logger;
        private readonly List<string> _refusals = [];

        /// <summary>How many files the turn had already stored when this call began.</summary>
        /// <remarks>
        /// The turn may call the agent more than once — "a docx and a deck" is two runs — and both
        /// this object and the lease's file list outlive one call. Without this, the second call
        /// reports the first call's files, including when it produced none of its own.
        /// </remarks>
        private int _storedBeforeThisRun;

        /// <summary>The hosted files the sandbox mounts, mutated in place before each run.</summary>
        public List<AIContent> Inputs { get; } = [];

        public async Task<AgentResponse> RunAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? runOptions,
            AIAgent inner,
            CancellationToken cancellationToken)
        {
            var instruction = string.Join(
                ' ', messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text));

            // Before the inner run, so the skills provider's filter — which advertises only the
            // formats this instruction names — sees it when the run consults it.
            _instruction.Text = instruction;

            _refusals.Clear();
            _storedBeforeThisRun = _lease.Produced.Count;

            // Bounds the whole invocation, sandbox round trips included, independently of the outer
            // turn's own limits. Linked, so a cancelled turn still stops this promptly.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(_options.ToolTimeoutSeconds));

            try
            {
                await MountAsync(instruction, deadline.Token).ConfigureAwait(false);

                var response = await inner.RunAsync(messages, session, runOptions, deadline.Token).ConfigureAwait(false);

                await StoreAsync(response, deadline.Token).ConfigureAwait(false);

                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The run's own deadline rather than the turn's. Named, because a generic timeout
                // message gets the wrong knob turned.
                _logger.LogWarning(
                    "The File Agent run on conversation {ConversationId} exceeded its {Timeout}s bound",
                    _conversationId, _options.ToolTimeoutSeconds);

                throw new InvalidOperationException(
                    $"Building the file took longer than the {_options.ToolTimeoutSeconds} second limit and was stopped. "
                        + "Tell the user, and suggest a smaller or simpler file.");
            }
        }

        /// <summary>
        /// Turns the run's outcome into what the assistant reads back.
        /// </summary>
        /// <remarks>
        /// A run that stored nothing is reported as such rather than thrown: the agent legitimately
        /// answers without a file when it refuses a request — an unsupported conversion, an ambiguous
        /// file name — and turning that into a tool failure would make a correct refusal look like a
        /// broken one. Telling those two apart is a separate concern from producing the file.
        /// </remarks>
        public string Describe(object? agentText)
        {
            var text = agentText?.ToString() ?? string.Empty;
            var refusals = _refusals.Count == 0 ? string.Empty : $" {string.Join(" ", _refusals)}";
            List<MessageAttachmentDto> stored = [.. _lease.Produced.Skip(_storedBeforeThisRun)];

            if (stored.Count == 0)
            {
                return $"{text}\n\n[No file was saved.{refusals} Do not describe a file as though the user has one.]";
            }

            var names = string.Join(", ", stored.Select(file => file.Name));

            return $"{text}\n\n[Saved and attached to your answer: {names}.{refusals}]";
        }

        private async Task MountAsync(string instruction, CancellationToken cancellationToken)
        {
            Inputs.Clear();

            var sources = await _documentReader
                .ReadMentionedAsync(_conversationId, _userId, instruction, _options.MaxArtifactsPerRun, cancellationToken)
                .ConfigureAwait(false);

            foreach (var source in sources)
            {
                var uploaded = await _lease.Sandbox
                    .UploadAsync(source.Name, source.MimeType, source.Content, cancellationToken)
                    .ConfigureAwait(false);

                Inputs.Add(uploaded);
            }

            if (sources.Count > 0)
            {
                ChatProgress.Report($"Loading {sources.Count} file(s)…");
            }
        }

        private async Task StoreAsync(AgentResponse response, CancellationToken cancellationToken)
        {
            var artifacts = FileAgentSandbox.Harvest(response.Messages);

            if (artifacts.Count == 0)
            {
                return;
            }

            if (artifacts.Count > _options.MaxArtifactsPerRun)
            {
                _refusals.Add(
                    $"{artifacts.Count - _options.MaxArtifactsPerRun} further file(s) the run produced were not saved, "
                        + $"because one run may return {_options.MaxArtifactsPerRun}.");
            }

            foreach (var artifact in artifacts.Take(_options.MaxArtifactsPerRun))
            {
                if (artifact.Name is not { Length: > 0 } name)
                {
                    // No name means no extension, and the store keeps only the seven formats it can
                    // name. Refused here so the reason names the real problem rather than the
                    // placeholder a fallback would have invented.
                    _refusals.Add("One file could not be saved because the sandbox gave it no name.");
                    continue;
                }

                try
                {
                    var bytes = await _lease.Sandbox.DownloadAsync(artifact, cancellationToken).ConfigureAwait(false);

                    var stored = await _generatedDocuments
                        .StoreAsync(new GeneratedDocumentRequest(_conversationId, _userId, name, bytes), cancellationToken)
                        .ConfigureAwait(false);

                    _lease.Produced.Add(stored);
                    ChatProgress.Report($"Saved {stored.Name}");
                }
                catch (ValidationException exception)
                {
                    // The store's own sanitized sentence — over the size ceiling, or a format this
                    // platform does not keep.
                    _refusals.Add(exception.Errors.FirstOrDefault()?.ErrorMessage ?? exception.Message);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Per artifact rather than per run: a file already stored is already on its way
                    // to the answer's chips, so failing the whole call here would tell the model
                    // nothing was saved while the user is looking at one that was.
                    _logger.LogError(
                        exception, "Could not store an artifact from the File Agent run on conversation {ConversationId}", _conversationId);
                    _refusals.Add($"'{name}' could not be saved.");
                }
            }
        }
    }
}
#pragma warning restore MEAI001
