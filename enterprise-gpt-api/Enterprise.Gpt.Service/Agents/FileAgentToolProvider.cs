using System.Diagnostics;
using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Dto.Conversations;
using Enterprise.Gpt.Service.Agents.Constants;
using Enterprise.Gpt.Service.Agents.Models;
using Enterprise.Gpt.Service.Conversations.Chat.Exceptions;
using Enterprise.Gpt.Service.Documents;
using Enterprise.Gpt.Service.Documents.Models;
using Enterprise.Gpt.Service.McpServers;
using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Prompts;
using FluentValidation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>The File Agent, leased for the life of one turn.</summary>
/// <remarks>Disposal releases the sandbox uploads, so an undisposed lease leaks file quota.</remarks>
public interface IFileAgentToolLease : IAsyncDisposable
{
    /// <summary>Gets the agent, wrapped as one tracked tool for the assistant's tool list.</summary>
    AITool Tool { get; }

    /// <summary>Gets the catalog row the agent ran on, as it stood when the turn borrowed it.</summary>
    /// <remarks>Recorded on the agent tool-call row, the only usage row that names its model.</remarks>
    FileAgentModel Model { get; }

    /// <summary>
    /// Gets the files the agent produced and stored during this turn, in the order it produced them.
    /// </summary>
    IReadOnlyList<MessageAttachmentDto> GeneratedDocuments { get; }

    /// <summary>Withdraws every file this turn stored, for a turn that will not deliver them.</summary>
    /// <remarks>
    /// The transcript references generated files only on a completed turn, so leftovers would be orphans.
    /// Leaves <see cref="GeneratedDocuments"/> empty, and never throws.
    /// </remarks>
    Task DiscardGeneratedAsync();
}

/// <summary>Composes the File Agent for a turn.</summary>
/// <remarks>Mirrors <see cref="IMcpToolProvider"/>: a turn borrows a tool and gives it back.</remarks>
public interface IFileAgentToolProvider
{
    /// <summary>Builds the agent for one turn; the caller's ownership decides which files it reads.</summary>
    /// <exception cref="Exceptions.FileAgentNotConfiguredException">
    /// The pinned model row is absent, deactivated, or on a provider without a hosted code interpreter.
    /// </exception>
    /// <exception cref="ProviderNotConfiguredException">No chat client is registered for the agent.</exception>
    Task<IFileAgentToolLease> AcquireAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// Agent-level middleware, because <c>WithTracking</c>'s string bridge hides the response that identifies
/// produced files. See <c>docs/tools/file-agent.md</c>.
/// </remarks>
// Hosted files are experimental in Microsoft.Extensions.AI 10.9.0; suppressed per file, not project-wide.
#pragma warning disable MEAI001
public sealed partial class FileAgentToolProvider(
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
        + "work from exactly as they appear in the conversation and no others — to create a new file, "
        + "describe what it should contain rather than inventing a name for it. Returns a sentence "
        + "describing what it made; the file itself is attached to your answer automatically.";

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

        // The model writes the instruction later, so the skills filter reads it through this.
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

            // Shared across the turn's calls; mutating Inputs is safe only because AllowConcurrentInvocation
            // is false on both clients.
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

                    // Already a full pipeline; false would write this turn's tools onto the shared
                    // client's function invoker for the next turn to inherit.
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

                        // Runs after the middleware stored the output, so the assistant reads the
                        // outcome, not just the agent's prose.
                        MarshalResult = (result, _, _) => new ValueTask<object?>(run.Describe(result))
                    },

                    // The agent's client has no tracking of its own; if it did, this would double every
                    // agent token.
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
            // Cleared first, so GeneratedDocuments reads empty whatever the withdrawals do.
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
    private sealed partial class FileAgentRun(
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

        // A turn may call the agent twice and the lease's file list outlives a call, so each call reports
        // only what it stored.
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

            // Before the inner run, so the skills filter sees it.
            _instruction.Text = instruction;

            _refusals.Clear();
            _rejectedArtifacts.Clear();
            _storedBeforeThisRun = _lease.Stored.Count;

            // Bounds the whole invocation independently of the turn; linked so a cancelled turn still stops it.
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

                LogFileAgentRunTimedOut(_logger, _conversationId, _options.ToolTimeoutSeconds);

                throw new InvalidOperationException(failure.Message);
            }
            catch (OperationCanceledException)
            {
                outcome = FileAgentOutcomes.Cancelled;
                throw;
            }
            catch (Exception exception)
            {
                // Replaced, not rethrown: IncludeDetailedErrors writes this into the chat history, and SQL or
                // storage exceptions name servers, containers and blob paths.
                var failure = FileAgentFailures.Unexpected();
                outcome = Fail(failure);

                LogFileAgentRunFailed(_logger, exception, _conversationId);

                throw new InvalidOperationException(failure.Message);
            }
            finally
            {
                ChatMetrics.RecordFileAgentRun(outcome, Stopwatch.GetElapsedTime(started));
                FileAgentTracing.Complete(span, outcome);
            }
        }

        // Reached only for a run that returned, so "no file" here is a refusal; failed runs throw instead.
        public string Describe(object? agentText)
        {
            var text = agentText?.ToString() ?? string.Empty;

            // Both lists, so a run that saved one file and rejected another mentions both.
            List<string> unsaved = [.. _rejectedArtifacts, .. _refusals];
            var refusals = unsaved.Count == 0 ? string.Empty : $" {string.Join(" ", unsaved)}";
            List<StoredArtifact> stored = [.. _lease.Stored.Skip(_storedBeforeThisRun)];

            if (stored.Count == 0)
            {
                return $"{text}\n\n[No file was saved.{refusals} Do not describe a file as though the user has one.]";
            }

            // The measured shape, not the requested one, so a quoting answer states what the file holds.
            var names = string.Join(", ", stored.Select(artifact => $"{artifact.File.Name} ({artifact.Shape})"));

            return $"{text}\n\n[Saved and attached to your answer: {names}.{refusals}]";
        }

        #region Private methods

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
                // Answered rather than failed, so a clean refusal does not look like a broken tool call;
                // the outcome tag separates them in telemetry.
                ChatProgress.Report(refusal.SubStatus);

                return (new AgentResponse(new ChatMessage(ChatRole.Assistant, refusal.Text)), refusal.Outcome);
            }

            await MountAsync(resolution, cancellationToken).ConfigureAwait(false);

            var attempts = _options.MaxVerificationRetries + 1;
            var reachedSandbox = false;

            // Survive the per-attempt clear, so a failed check followed by an empty attempt still reports
            // verification failure and what did not match.
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

                // Only a failed check earns a retry; a run that produced nothing would likely do so again.
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

                // So the next attempt is described on its own terms.
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
                    // No name means no extension; refused here so the reason names the real problem.
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
            // A constant label: the scope's ToolName is persisted on a usage row, while the sub-status
            // carrying the file name is not.
            using var scope = ChatProgress.BeginToolScope(Step("Checking the file"), owner: null);

            ChatProgress.Report($"Checking {Path.GetFileName(name)}");

            try
            {
                var bytes = await _lease.Sandbox.DownloadAsync(artifact, cancellationToken).ConfigureAwait(false);

                // Before the store, so an artifact that does not re-open never becomes a row, and the
                // check covers the exact bytes the user will download.
                var checkResult = _verifier.Verify(name, bytes);

                // Clamped: the extension is model-chosen and a metric dimension must stay low-cardinality.
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
                // The store's own sanitized reason (too large, or an unsupported format).
                _refusals.Add(exception.Errors.FirstOrDefault()?.ErrorMessage ?? exception.Message);
                scope.Fail();

                return false;
            }
            catch (OperationCanceledException)
            {
                // Its own arm, or the step would persist as succeeded on a turn the user stopped.
                scope.Fail();
                throw;
            }
            catch (Exception exception)
            {
                // Per artifact: failing the call would tell the model nothing was saved while an
                // already-stored file is shown to the user.
                LogCouldNotStoreArtifact(_logger, exception, _conversationId);
                _refusals.Add($"'{name}' could not be saved.");
                scope.Fail();

                return false;
            }
        }

        private string Detail() => string.Join(" ", _rejectedArtifacts.Concat(_refusals));

        /// <summary>Reports a failure's line to the card and returns its outcome, so both agree.</summary>
        private static string Fail(FileAgentFailure failure)
        {
            ChatProgress.Report(failure.SubStatus);

            return failure.Outcome;
        }

        private string RetryInstruction() =>
            $"The file you produced did not pass the check that runs before anything is saved: "
            + $"{string.Join(" ", _rejectedArtifacts)} Fix the code that wrote it and produce the file again. "
            + "Do not answer until it opens cleanly.";

        // Classifies a run that stored nothing. reachedSandbox is logged to tell a run that never ran code
        // from one whose code wrote nothing.
        private FileAgentRunFailure Unproduced(bool reachedSandbox, bool anyRejected, string rejection)
        {
            var detail = Detail();

            if (anyRejected)
            {
                return new FileAgentRunFailure(
                    FileAgentFailures.VerificationFailed(detail.Length > 0 ? detail : rejection));
            }

            LogFileAgentRunProducedNoFile(_logger, _conversationId, reachedSandbox);

            return new FileAgentRunFailure(FileAgentFailures.NoFileProduced(detail));
        }

        // One step of the run, as its own card. Prose in Name too: the card renders Name, not DisplayName.
        private static ToolDescriptor Step(string label) =>
            new() { Name = label, DisplayName = label, Kind = ToolKind.Agent };

        private static bool ReachedSandbox(AgentResponse response) =>
            response.Messages
                .SelectMany(message => message.Contents)
                .Any(content => content is CodeInterpreterToolCallContent or CodeInterpreterToolResultContent);

        #endregion

        #region Logging

        [LoggerMessage(Level = LogLevel.Warning, Message = "The File Agent run on conversation {ConversationId} exceeded its {Timeout}s bound")]
        private static partial void LogFileAgentRunTimedOut(ILogger logger, Guid conversationId, int timeout);

        [LoggerMessage(Level = LogLevel.Error, Message = "The File Agent run on conversation {ConversationId} failed")]
        private static partial void LogFileAgentRunFailed(ILogger logger, Exception exception, Guid conversationId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Could not store an artifact from the File Agent run on conversation {ConversationId}")]
        private static partial void LogCouldNotStoreArtifact(ILogger logger, Exception exception, Guid conversationId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The File Agent run on conversation {ConversationId} produced no file (reached the sandbox: {ReachedSandbox})")]
        private static partial void LogFileAgentRunProducedNoFile(
            ILogger logger, Guid conversationId, bool reachedSandbox);

        #endregion
    }

    // A terminal outcome carried out of the run's frames; FileAgentRun.RunAsync converts it into the
    // sanitized InvalidOperationException the tool contract uses.
    private sealed class FileAgentRunFailure(FileAgentFailure failure) : Exception(failure.Message)
    {
        public FileAgentFailure Failure { get; } = failure;
    }
}
