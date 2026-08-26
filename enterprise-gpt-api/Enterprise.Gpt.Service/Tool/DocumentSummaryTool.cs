using Andes.Extensions.AI;
using Enterprise.Gpt.Service.Summarization;
using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// The result of one <c>document_summarize</c> call, serialized straight back to the model.
/// </summary>
public sealed class DocumentSummaryToolResult
{
    /// <summary>
    /// Gets or sets what was summarized: <c>document</c> for a single file, <c>digest</c> for every
    /// document the conversation can see.
    /// </summary>
    [JsonPropertyOrder(0)]
    public required string Scope { get; set; }

    /// <summary>
    /// Gets or sets the name of the document that was summarized, or <see langword="null"/> for a
    /// digest.
    /// </summary>
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentName { get; set; }

    /// <summary>
    /// Gets or sets how many documents the summary covers.
    /// </summary>
    [JsonPropertyOrder(2)]
    public int DocumentCount { get; set; }

    /// <summary>
    /// Gets or sets the summary text, or <see langword="null"/> when no summary was produced
    /// because the requested document could not be identified.
    /// </summary>
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>
    /// Gets or sets the names of the documents available to summarize. Populated only when
    /// <see cref="Summary"/> is absent, so the model can name one correctly on a second attempt.
    /// </summary>
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AvailableDocuments { get; set; }

    /// <summary>
    /// Gets or sets a short explanation of an absent or unusual result, for the model rather than
    /// the user.
    /// </summary>
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

/// <summary>
/// Exposes document summarization to the assistant as a callable function.
/// </summary>
/// <remarks>
/// <para>
/// Built per turn and closed over the corpus resolved for that turn, so a summary can only ever be
/// produced for a document the caller was already entitled to read. Nothing about the conversation
/// or the user comes from the tool's arguments, which means a model that hallucinates one cannot
/// widen what it sees.
/// </para>
/// <para>
/// Unlike <see cref="DocumentTool"/>, this one spends real money on a cache miss, which is why an
/// ambiguous document name refuses rather than widening: search widens on ambiguity because a wider
/// search is cheap, and summarizing the wrong document is not.
/// </para>
/// </remarks>
public static class DocumentSummaryTool
{
    /// <summary>
    /// The name the model calls this tool by, and the name it appears under in the activity feed
    /// and the usage audit.
    /// </summary>
    /// <remarks>
    /// Shares <see cref="DocumentTool.ToolName"/>'s leading segment for the same reason it was
    /// chosen: tool-call usage is attributed to an MCP server by matching the longest
    /// <c>{server}_</c> prefix, so a name such as <c>summarize_document</c> would be credited to a
    /// server called "summarize" if one ever existed.
    /// </remarks>
    public const string ToolName = "document_summarize";

    private const string ToolDescription =
        "Produces a summary of the documents attached to this conversation. Pass a file name to summarize " +
        "one document; omit it to summarize every attached document at once. Use this when the user asks " +
        "what a document says, what it is about, or for an overview of their files — not when they ask a " +
        "specific question about their contents, which " + DocumentTool.ToolName + " answers better and more " +
        "cheaply. The first summary of a document takes a while; later ones are instant.";

    /// <summary>
    /// Builds the summarization function for one turn.
    /// </summary>
    /// <param name="scope">The corpus this turn may summarize, from <see cref="IDocumentRetrievalService.GetScopeAsync"/>.</param>
    /// <param name="conversationId">The conversation every model call this tool makes is billed to.</param>
    /// <param name="summaryService">The service backing the tool.</param>
    /// <param name="toolTimeoutSeconds">How long one whole invocation may run before it is abandoned.</param>
    /// <param name="logger">Logger for failures that must not reach the model verbatim.</param>
    /// <returns>The function to attach to <see cref="ChatOptions.Tools"/>.</returns>
    /// <exception cref="ArgumentNullException">Any reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toolTimeoutSeconds"/> is not positive.</exception>
    public static AIFunction Create(
        DocumentRetrievalScope scope,
        Guid conversationId,
        IDocumentSummaryService summaryService,
        int toolTimeoutSeconds,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(summaryService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toolTimeoutSeconds);

        var invoker = new DocumentSummaryInvoker(scope, conversationId, summaryService, toolTimeoutSeconds, logger);

        return AIFunctionFactory.Create(invoker.SummarizeAsync, ToolName, ToolDescription);
    }

    /// <summary>
    /// Holds the per-turn state the function body needs, so the state is captured once rather than
    /// resolved on every call.
    /// </summary>
    private sealed class DocumentSummaryInvoker(
        DocumentRetrievalScope scope,
        Guid conversationId,
        IDocumentSummaryService summaryService,
        int toolTimeoutSeconds,
        ILogger logger)
    {
        private readonly DocumentRetrievalScope _scope = scope;
        private readonly Guid _conversationId = conversationId;
        private readonly IDocumentSummaryService _summaryService = summaryService;
        private readonly int _toolTimeoutSeconds = toolTimeoutSeconds;
        private readonly ILogger _logger = logger;

        /// <summary>
        /// Summarizes one document, or every document in the conversation.
        /// </summary>
        /// <remarks>
        /// Progress is reported through the ambient <see cref="ChatProgress"/> reporter, which is
        /// the only channel that reaches the activity feed. It matters more here than it does for
        /// search: this runs inline on a turn and a first summary can take a while, so a silent
        /// tool would look like a stalled answer.
        /// </remarks>
        public async Task<DocumentSummaryToolResult> SummarizeAsync(
            [Description("The file name of the document to summarize. Omit it to summarize every document in the conversation at once.")]
            string? documentName = null,
            CancellationToken cancellationToken = default)
        {
            // Bounds the whole invocation, where SummarizationOptions.CallTimeoutSeconds bounds one
            // call inside it. Linked, so the turn being torn down still cancels promptly.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(_toolTimeoutSeconds));

            try
            {
                return string.IsNullOrWhiteSpace(documentName)
                    ? await SummarizeAllAsync(deadline.Token).ConfigureAwait(false)
                    : await SummarizeOneAsync(documentName.Trim(), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The turn is being torn down; let cancellation stay cancellation rather than
                // turning it into a tool error the model would try to work around.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // The deadline above, not the caller. Reported to the model rather than thrown at
                // the turn, because a summary that took too long is something the user can be told.
                _logger.LogWarning(
                    ex,
                    "Summarization for conversation {ConversationId} exceeded its {TimeoutSeconds}s tool deadline.",
                    _conversationId, _toolTimeoutSeconds);

                throw new InvalidOperationException(
                    "Summarizing took too long and was stopped. Tell the user the document was too large to "
                        + "summarize in a conversation, and offer to answer specific questions about it instead.");
            }
            catch (ValidationException ex)
            {
                // The engine's and the summary service's own terminal failures already carry a
                // sanitized, user-facing sentence — that is what ValidationException means on this
                // path — so it is relayed rather than replaced.
                var message = ex.Errors.FirstOrDefault()?.ErrorMessage ?? "This document could not be summarized.";

                _logger.LogInformation(
                    "Summarization refused for conversation {ConversationId}: {Reason}", _conversationId, message);

                throw new InvalidOperationException(
                    $"{message} Tell the user this, and do not try to summarize the same thing again.");
            }
            catch (Exception ex)
            {
                // Function invocation is configured with detailed errors, so whatever is thrown here
                // is handed to the model verbatim. The real exception goes to the log; the model
                // gets a sentence it can act on and nothing about the machinery behind it.
                _logger.LogError(
                    ex, "Summarization failed for conversation {ConversationId}.", _conversationId);

                throw new InvalidOperationException(
                    "The summary could not be produced. Tell the user their documents could not be summarized, "
                        + "and offer to answer specific questions about them instead.");
            }
        }

        private async Task<DocumentSummaryToolResult> SummarizeOneAsync(
            string documentName, CancellationToken cancellationToken)
        {
            var matches = DocumentRetrievalService.MatchByName(_scope, documentName);

            if (matches.Count != 1)
            {
                // Refused rather than widened to a digest, and refused before any model call: a
                // near-miss on a file name must not spend a run on the wrong document, or on every
                // document, when asking again costs the model one cheap turn.
                return new DocumentSummaryToolResult
                {
                    Scope = "document",
                    DocumentCount = 0,
                    AvailableDocuments = _scope.DocumentNames,
                    Note = matches.Count == 0
                        ? $"No document is named '{documentName}'. Ask again with one of the available names, "
                            + "or omit the name to summarize all of them."
                        : $"'{documentName}' matches more than one document. Ask again with a full name."
                };
            }

            var document = matches[0];

            ChatProgress.Report($"Summarizing {document.Name}…");

            var summary = await _summaryService
                .GetOrCreateAsync(_conversationId, document.Id, document.Source, cancellationToken)
                .ConfigureAwait(false);

            ChatProgress.Report(summary.FromCache ? "Reusing an existing summary" : "Summary ready");

            return new DocumentSummaryToolResult
            {
                Scope = "document",
                DocumentName = document.Name,
                DocumentCount = 1,
                Summary = summary.Text
            };
        }

        private async Task<DocumentSummaryToolResult> SummarizeAllAsync(CancellationToken cancellationToken)
        {
            // A single document in scope is its own digest. Routing it through the digest path would
            // buy a second model call to reduce one summary to itself.
            if (_scope.Documents.Count == 1)
            {
                return await SummarizeOneAsync(_scope.Documents[0].Name, cancellationToken).ConfigureAwait(false);
            }

            ChatProgress.Report(
                $"Summarizing {_scope.Documents.Count} documents…", progress: 0, progressTotal: _scope.Documents.Count);

            var digest = await _summaryService
                .CreateDigestAsync(_conversationId, _scope, cancellationToken)
                .ConfigureAwait(false);

            ChatProgress.Report("Summary ready");

            return new DocumentSummaryToolResult
            {
                Scope = "digest",
                DocumentCount = _scope.Documents.Count,
                Summary = digest
            };
        }
    }
}
