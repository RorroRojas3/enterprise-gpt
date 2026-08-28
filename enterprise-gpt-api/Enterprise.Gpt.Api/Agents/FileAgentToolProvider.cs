using System.Diagnostics;
using Andes.Extensions.AI;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Observability;
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
/// The agent is reached through agent-level middleware, because <c>WithTracking</c>'s string-in/
/// string-out bridge hides the response a produced file's identity appears on. See
/// <c>docs/file-agent/the-agent.md</c> §5 for that choice and the two it beat, and §3 for why the
/// agent's client is its own.
/// </remarks>
// The hosted-file client is still marked experimental in Microsoft.Extensions.AI 10.9.0. Suppressed
// on this one file, as in FileAgentSandbox, rather than project-wide.
#pragma warning disable MEAI001
public sealed class FileAgentToolProvider(
    ILoggerFactory loggerFactory,
    IFileAgentModelResolver modelResolver,
    IFileAgentDocumentReader documentReader,
    IGeneratedDocumentService generatedDocuments,
    IGeneratedArtifactVerifier verifier,
    IConversionMatrix conversionMatrix,
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
    private readonly IGeneratedArtifactVerifier _verifier = verifier;
    private readonly IConversionMatrix _conversionMatrix = conversionMatrix;
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
            FileAgentSkills.CreateProvider(FileAgentSkills.DeployedRoot, () => instruction.Text, _loggerFactory),
            model,
            _generatedDocuments);

        try
        {
            var run = new FileAgentRun(
                lease,
                instruction,
                _documentReader,
                _generatedDocuments,
                _verifier,
                _conversionMatrix,
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
    private sealed class FileAgentToolLease(
        FileAgentSandbox sandbox,
        AgentSkillsProvider skills,
        FileAgentModel model,
        IGeneratedDocumentService generatedDocuments) : IFileAgentToolLease
    {
        private readonly IGeneratedDocumentService _generatedDocuments = generatedDocuments;
        private int _disposed;

        public FileAgentSandbox Sandbox { get; } = sandbox;

        public AgentSkillsProvider Skills { get; } = skills;

        public AITool Tool { get; set; } = null!;

        /// <inheritdoc />
        public FileAgentModel Model { get; } = model;

        /// <summary>What this turn stored, with what re-opening each one measured.</summary>
        public List<StoredArtifact> Stored { get; } = [];

        /// <inheritdoc />
        public IReadOnlyList<MessageAttachmentDto> GeneratedDocuments => [.. Stored.Select(stored => stored.File)];

        /// <inheritdoc />
        public async Task DiscardGeneratedAsync()
        {
            // Copied and cleared first, so a caller that reads GeneratedDocuments after this sees the
            // turn's real answer — nothing — whatever the withdrawals themselves do.
            List<StoredArtifact> withdrawing = [.. Stored];
            Stored.Clear();

            foreach (var artifact in withdrawing)
            {
                await _generatedDocuments.DiscardAsync(artifact.File.Id).ConfigureAwait(false);
            }
        }

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

    /// <summary>A file this turn stored, and what re-opening it found.</summary>
    private sealed record StoredArtifact(MessageAttachmentDto File, string Shape);

    /// <summary>The instruction the model wrote for this run, once it has written one.</summary>
    private sealed class RunInstruction
    {
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// The middleware around one agent run: refuses what cannot be served, mounts the files its
    /// instruction names, checks what it produced, and stores what survived.
    /// </summary>
    private sealed class FileAgentRun(
        FileAgentToolLease lease,
        RunInstruction instruction,
        IFileAgentDocumentReader documentReader,
        IGeneratedDocumentService generatedDocuments,
        IGeneratedArtifactVerifier verifier,
        IConversionMatrix conversionMatrix,
        FileAgentOptions options,
        Guid conversationId,
        Guid userId,
        ILogger logger)
    {
        private readonly FileAgentToolLease _lease = lease;
        private readonly RunInstruction _instruction = instruction;
        private readonly IFileAgentDocumentReader _documentReader = documentReader;
        private readonly IGeneratedDocumentService _generatedDocuments = generatedDocuments;
        private readonly IGeneratedArtifactVerifier _verifier = verifier;
        private readonly IConversionMatrix _conversionMatrix = conversionMatrix;
        private readonly FileAgentOptions _options = options;
        private readonly Guid _conversationId = conversationId;
        private readonly Guid _userId = userId;
        private readonly ILogger _logger = logger;
        private readonly List<string> _refusals = [];
        private readonly List<string> _rejectedArtifacts = [];

        // A turn may call the agent twice — "a docx and a deck" — and both this object and the lease's
        // file list outlive one call, so without this the second call reports the first call's files.
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
            List<ChatMessage> conversation = [.. messages];
            var instruction = string.Join(
                ' ', conversation.Where(message => message.Role == ChatRole.User).Select(message => message.Text));

            // Before the inner run, so the skills provider's filter — which advertises only the
            // formats this instruction names — sees it when the run consults it.
            _instruction.Text = instruction;

            _refusals.Clear();
            _rejectedArtifacts.Clear();
            _storedBeforeThisRun = _lease.Stored.Count;

            // Bounds the whole invocation, sandbox round trips included, independently of the outer
            // turn's own limits. Linked, so a cancelled turn still stops this promptly.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(_options.ToolTimeoutSeconds));

            using var span = FileAgentTracing.StartRun();
            var started = Stopwatch.GetTimestamp();
            var outcome = FileAgentOutcomes.Error;

            try
            {
                var (response, result) = await ExecuteAsync(
                        conversation, session, runOptions, inner, instruction, deadline.Token)
                    .ConfigureAwait(false);

                outcome = result;

                return response;
            }
            catch (FileAgentRunFailure classified)
            {
                outcome = Fail(classified.Failure);

                throw new InvalidOperationException(classified.Failure.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The run's own deadline rather than the turn's.
                var failure = FileAgentFailures.TimedOut(_options.ToolTimeoutSeconds);
                outcome = Fail(failure);

                _logger.LogWarning(
                    "The File Agent run on conversation {ConversationId} exceeded its {Timeout}s bound",
                    _conversationId, _options.ToolTimeoutSeconds);

                throw new InvalidOperationException(failure.Message);
            }
            catch (OperationCanceledException)
            {
                outcome = FileAgentOutcomes.Cancelled;
                throw;
            }
            catch (Exception exception)
            {
                // Replaced rather than rethrown: the outer client's IncludeDetailedErrors writes this
                // message into the chat history, and a SQL or storage exception carries a server, a
                // container and a blob path with it.
                var failure = FileAgentFailures.Unexpected();
                outcome = Fail(failure);

                _logger.LogError(
                    exception, "The File Agent run on conversation {ConversationId} failed", _conversationId);

                throw new InvalidOperationException(failure.Message);
            }
            finally
            {
                ChatMetrics.RecordFileAgentRun(outcome, Stopwatch.GetElapsedTime(started));
                FileAgentTracing.Complete(span, outcome);
            }
        }

        /// <summary>
        /// Turns the run's outcome into what the assistant reads back.
        /// </summary>
        /// <remarks>
        /// Reached only for a run that returned rather than threw, so the "no file" wording here
        /// belongs to a refusal the agent made — an unsupported conversion, an ambiguous name — and
        /// never to a run that failed to produce one, which throws instead.
        /// </remarks>
        public string Describe(object? agentText)
        {
            var text = agentText?.ToString() ?? string.Empty;

            // Both lists, because a run that saved one file and rejected another would otherwise tell
            // the model about the one and leave the user wondering about the other.
            List<string> unsaved = [.. _rejectedArtifacts, .. _refusals];
            var refusals = unsaved.Count == 0 ? string.Empty : $" {string.Join(" ", unsaved)}";
            List<StoredArtifact> stored = [.. _lease.Stored.Skip(_storedBeforeThisRun)];

            if (stored.Count == 0)
            {
                return $"{text}\n\n[No file was saved.{refusals} Do not describe a file as though the user has one.]";
            }

            // The measured shape rather than the requested one, so an answer that quotes it is stating
            // what the file actually holds.
            var names = string.Join(", ", stored.Select(artifact => $"{artifact.File.Name} ({artifact.Shape})"));

            return $"{text}\n\n[Saved and attached to your answer: {names}.{refusals}]";
        }

        private async Task<(AgentResponse Response, string Outcome)> ExecuteAsync(
            List<ChatMessage> conversation,
            AgentSession? session,
            AgentRunOptions? runOptions,
            AIAgent inner,
            string instruction,
            CancellationToken cancellationToken)
        {
            var resolution = await _documentReader
                .ResolveAsync(_conversationId, _userId, instruction, cancellationToken)
                .ConfigureAwait(false);

            if (FileAgentPreflight.Evaluate(resolution, instruction, _conversionMatrix) is { } refusal)
            {
                // Answered rather than failed: the agent saying "I cannot do that, and here is why" is
                // a correct answer, and turning it into a failed tool call would make a clean refusal
                // look like a broken one. The outcome tag is what keeps the two apart in telemetry.
                ChatProgress.Report(refusal.SubStatus);

                return (new AgentResponse(new ChatMessage(ChatRole.Assistant, refusal.Text)), refusal.Outcome);
            }

            await MountAsync(resolution, cancellationToken).ConfigureAwait(false);

            var attempts = _options.MaxVerificationRetries + 1;
            var reachedSandbox = false;

            // Both survive the per-attempt clear below. Without them a run whose first attempt failed
            // its check and whose second produced nothing at all reports "no file produced" — and,
            // with only the flag, reports the right outcome with none of what did not match.
            var anyRejected = false;
            var rejection = string.Empty;

            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var response = await RunSandboxAsync(conversation, session, runOptions, inner, cancellationToken)
                    .ConfigureAwait(false);

                reachedSandbox |= ReachedSandbox(response);

                if (await StoreAsync(response, cancellationToken).ConfigureAwait(false))
                {
                    return (response, FileAgentOutcomes.Created);
                }

                if (_rejectedArtifacts.Count > 0)
                {
                    anyRejected = true;
                    rejection = Detail();
                }

                // Only a failed check earns another attempt. A run that produced nothing at all would
                // produce nothing again for the same reason, and paying for a second sandbox session
                // to find that out is the cost this guard avoids.
                if (_rejectedArtifacts.Count == 0 || attempt + 1 >= attempts)
                {
                    break;
                }

                conversation =
                [
                    .. conversation,
                    .. response.Messages,
                    new ChatMessage(ChatRole.User, RetryInstruction())
                ];

                // Both, so the next attempt is described on its own terms: an oversized file from this
                // one is not something to tell the user about once the retry has produced a good one.
                _rejectedArtifacts.Clear();
                _refusals.Clear();
            }

            throw Unproduced(reachedSandbox, anyRejected, rejection);
        }

        private async Task MountAsync(FileAgentSourceResolution resolution, CancellationToken cancellationToken)
        {
            Inputs.Clear();

            List<Guid> wanted = [.. resolution.Matched.Take(_options.MaxArtifactsPerRun).Select(match => match.Id)];

            if (wanted.Count == 0)
            {
                return;
            }

            using var scope = ChatProgress.BeginToolScope(Step("Preparing files"), owner: null);

            try
            {
                var sources = await _documentReader
                    .ReadAsync(_conversationId, _userId, wanted, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var source in sources)
                {
                    var uploaded = await _lease.Sandbox
                        .UploadAsync(source.Name, source.MimeType, source.Content, cancellationToken)
                        .ConfigureAwait(false);

                    Inputs.Add(uploaded);
                }

                ChatProgress.Report($"Loaded {sources.Count} file(s)");
            }
            catch
            {
                scope.Fail();
                throw;
            }
        }

        private static async Task<AgentResponse> RunSandboxAsync(
            List<ChatMessage> conversation,
            AgentSession? session,
            AgentRunOptions? runOptions,
            AIAgent inner,
            CancellationToken cancellationToken)
        {
            using var scope = ChatProgress.BeginToolScope(Step("Running code"), owner: null);

            var started = Stopwatch.GetTimestamp();
            var outcome = FileAgentOutcomes.Succeeded;

            ChatMetrics.RecordFileAgentSandboxActive(1);

            try
            {
                return await inner.RunAsync(conversation, session, runOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = FileAgentOutcomes.Cancelled;
                scope.Fail();
                throw;
            }
            catch
            {
                outcome = FileAgentOutcomes.Error;
                scope.Fail();
                throw;
            }
            finally
            {
                ChatMetrics.RecordFileAgentSandboxActive(-1);
                ChatMetrics.RecordFileAgentSandbox(outcome, Stopwatch.GetElapsedTime(started));
            }
        }

        /// <summary>
        /// Downloads, checks and stores what the run produced.
        /// </summary>
        /// <returns><see langword="true"/> when at least one artifact was verified and stored.</returns>
        private async Task<bool> StoreAsync(AgentResponse response, CancellationToken cancellationToken)
        {
            var artifacts = FileAgentSandbox.Harvest(response.Messages);

            if (artifacts.Count == 0)
            {
                return false;
            }

            if (artifacts.Count > _options.MaxArtifactsPerRun)
            {
                _refusals.Add(
                    $"{artifacts.Count - _options.MaxArtifactsPerRun} further file(s) the run produced were not saved, "
                        + $"because one run may return {_options.MaxArtifactsPerRun}.");
            }

            var storedAny = false;

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

                storedAny |= await StoreOneAsync(artifact, name, cancellationToken).ConfigureAwait(false);
            }

            return storedAny;
        }

        private async Task<bool> StoreOneAsync(
            SandboxArtifact artifact, string name, CancellationToken cancellationToken)
        {
            // A constant, with the file name on the ephemeral sub-status instead: a nested scope becomes
            // a ToolCallUsage child, and the audit row it writes keeps its ToolName for ever, where a
            // progress line is never persisted.
            using var scope = ChatProgress.BeginToolScope(Step("Checking the file"), owner: null);

            ChatProgress.Report($"Checking {Path.GetFileName(name)}");

            try
            {
                var bytes = await _lease.Sandbox.DownloadAsync(artifact, cancellationToken).ConfigureAwait(false);

                // Before the store, never after: an artifact that does not re-open must not become a
                // row and a chip, and re-opening the bytes on their way to storage checks the file the
                // user will actually download rather than a copy left in the container.
                var checkResult = _verifier.Verify(name, bytes);

                // Clamped to the seven this platform produces: the extension comes off a name the model
                // chose, and a metric dimension is retained, high-cardinality storage.
                var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

                ChatMetrics.RecordFileAgentVerification(
                    FileAgentFormats.Producible.Contains(extension) ? extension : "other", checkResult.Passed);

                if (!checkResult.Passed)
                {
                    _rejectedArtifacts.Add($"'{name}' did not open when checked: {checkResult.Reason}.");
                    ChatProgress.Report($"{name} did not open when checked");
                    scope.Fail();

                    return false;
                }

                var stored = await _generatedDocuments
                    .StoreAsync(new GeneratedDocumentRequest(_conversationId, _userId, name, bytes), cancellationToken)
                    .ConfigureAwait(false);

                _lease.Stored.Add(new StoredArtifact(stored, checkResult.Shape));
                ChatProgress.Report($"Saved {stored.Name} — {checkResult.Shape}");

                return true;
            }
            catch (ValidationException exception)
            {
                // The store's own sanitized sentence — over the size ceiling, or a format this
                // platform does not keep.
                _refusals.Add(exception.Errors.FirstOrDefault()?.ErrorMessage ?? exception.Message);
                scope.Fail();

                return false;
            }
            catch (OperationCanceledException)
            {
                // Its own arm, ahead of the filter below: without it the handle is disposed un-failed
                // and the step reports — and persists — as succeeded on a turn the user stopped.
                scope.Fail();
                throw;
            }
            catch (Exception exception)
            {
                // Per artifact rather than per run: a file already stored is already on its way
                // to the answer's chips, so failing the whole call here would tell the model
                // nothing was saved while the user is looking at one that was.
                _logger.LogError(
                    exception, "Could not store an artifact from the File Agent run on conversation {ConversationId}", _conversationId);
                _refusals.Add($"'{name}' could not be saved.");
                scope.Fail();

                return false;
            }
        }

        /// <summary>Everything this attempt could not save, in one sentence.</summary>
        private string Detail() => string.Join(" ", _rejectedArtifacts.Concat(_refusals));

        /// <summary>Reports a failure's line to the card and returns its outcome, so both agree.</summary>
        private static string Fail(FileAgentFailure failure)
        {
            ChatProgress.Report(failure.SubStatus);

            return failure.Outcome;
        }

        /// <summary>The sentence that sends a failed check back for another attempt.</summary>
        private string RetryInstruction() =>
            $"The file you produced did not pass the check that runs before anything is saved: "
            + $"{string.Join(" ", _rejectedArtifacts)} Fix the code that wrote it and produce the file again. "
            + "Do not answer until it opens cleanly.";

        /// <summary>Classifies a run that stored nothing, which is always a failure by this point.</summary>
        /// <param name="anyRejected">Whether any attempt produced an artifact that failed its check.</param>
        /// <param name="rejection">
        /// What that check said, carried past the retry loop's own clear — the final attempt may have
        /// produced nothing at all, leaving the lists empty and the story with it.
        /// </param>
        private FileAgentRunFailure Unproduced(bool reachedSandbox, bool anyRejected, string rejection)
        {
            var detail = Detail();

            if (anyRejected)
            {
                return new FileAgentRunFailure(
                    FileAgentFailures.VerificationFailed(detail.Length > 0 ? detail : rejection));
            }

            _logger.LogWarning(
                "The File Agent run on conversation {ConversationId} produced no file (reached the sandbox: {ReachedSandbox})",
                _conversationId, reachedSandbox);

            return new FileAgentRunFailure(FileAgentFailures.NoFileProduced(detail));
        }

        /// <summary>One step of the run, as its own card beneath the agent's.</summary>
        /// <param name="label">
        /// Prose, and in <c>Name</c> rather than only <c>DisplayName</c>: the card reads its label off
        /// <c>Name</c>, and only the status line the tracker composes uses <c>DisplayName</c>. A code
        /// identifier in <c>Name</c> would put "run_code" on screen.
        /// </param>
        private static ToolDescriptor Step(string label) =>
            new() { Name = label, DisplayName = label, Kind = ToolKind.Agent };

        /// <summary>Whether the run actually opened a sandbox session, rather than answering from prose alone.</summary>
        private static bool ReachedSandbox(AgentResponse response) =>
            response.Messages
                .SelectMany(message => message.Contents)
                .Any(content => content is CodeInterpreterToolCallContent or CodeInterpreterToolResultContent);
    }

    /// <summary>
    /// A terminal outcome, carried out of the run's own frames so one place decides how it is reported.
    /// </summary>
    /// <remarks>
    /// Internal to this file and never observed by a caller: <see cref="FileAgentRun.RunAsync"/>
    /// converts it into the sanitized <see cref="InvalidOperationException"/> the tool contract uses,
    /// having first tagged the outcome and reported the line the activity card shows.
    /// </remarks>
    private sealed class FileAgentRunFailure(FileAgentFailure failure) : Exception(failure.Message)
    {
        public FileAgentFailure Failure { get; } = failure;
    }
}
#pragma warning restore MEAI001
