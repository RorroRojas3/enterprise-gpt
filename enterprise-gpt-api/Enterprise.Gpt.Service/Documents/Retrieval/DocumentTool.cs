using System.ComponentModel;
using Andes.Extensions.AI;
using Enterprise.Gpt.Service.Documents.Retrieval.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Documents.Retrieval;

/// <summary>
/// Exposes retrieval over a conversation's documents to the assistant as a callable function.
/// </summary>
/// <remarks>
/// Built per turn over the resolved scope; nothing about the conversation or user comes from tool
/// arguments, so a hallucinated argument cannot widen access. Attached only when there are documents,
/// since an always-empty tool teaches the model to stop calling it.
/// </remarks>
public static partial class DocumentTool
{
    /// <summary>
    /// The name the model calls this tool by, and the name it appears under in the activity feed and
    /// the usage audit.
    /// </summary>
    /// <remarks>
    /// Must not start with a possible MCP server name: usage is attributed by the longest <c>{server}_</c>
    /// prefix, so <c>search_documents</c> would be credited to a server called "search".
    /// </remarks>
    public const string ToolName = "document_search";

    private const string ToolDescription =
        "Searches the documents attached to this conversation and returns the passages most relevant to a query. " +
        "Use it whenever the user asks about their uploaded files, or about anything the documents might cover, " +
        "before answering from your own knowledge. Search more than once with different wording if the first " +
        "attempt returns nothing useful. Every passage comes back with a citation to quote.";

    private sealed partial class DocumentSearchInvoker(
        DocumentRetrievalScope scope,
        IDocumentRetrievalService retrievalService,
        ILogger logger)
    {
        private readonly DocumentRetrievalScope _scope = scope;
        private readonly IDocumentRetrievalService _retrievalService = retrievalService;
        private readonly ILogger _logger = logger;

        // ChatProgress is the only channel to the activity feed. Tool arguments are deliberately never
        // streamed, so the query must not appear in a progress message.
        public async Task<DocumentSearchResult> SearchAsync(
            [Description("What to look for. A natural-language question works best, but exact terms such as an error code or a part number are matched literally too.")]
            string query,
            [Description("Optional file name to restrict the search to a single document. Omit it to search every attached document.")]
            string? documentName = null,
            CancellationToken cancellationToken = default)
        {
            ChatProgress.Report(_scope.Documents.Count == 1
                ? "Searching 1 document…"
                : $"Searching {_scope.Documents.Count} documents…");

            try
            {
                var result = await _retrievalService.SearchAsync(_scope, query, documentName, cancellationToken).ConfigureAwait(false);

                ChatProgress.Report(result.ResultCount == 0
                    ? "No matching passages"
                    : result.ResultCount == 1
                        ? "Found 1 passage"
                        : $"Found {result.ResultCount} passages");

                return result;
            }
            catch (OperationCanceledException)
            {
                // Stays cancellation, not a tool error the model would try to work around.
                throw;
            }
            catch (Exception ex)
            {
                // Detailed errors are on, so the model sees this verbatim: the real exception goes to the
                // log, and the model gets an actionable sentence.
                LogSearchFailed(_logger, ex, _scope.ConversationId);

                throw new InvalidOperationException("The document search could not be completed. Answer from the conversation so far, and tell the user their documents could not be searched.");
            }
        }

        #region Logging

        [LoggerMessage(Level = LogLevel.Error, Message = "Document search failed for conversation {ConversationId}.")]
        private static partial void LogSearchFailed(ILogger logger, Exception exception, Guid conversationId);

        #endregion
    }

    #region Public static methods

    /// <summary>
    /// Builds the retrieval function for one turn.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static AIFunction Create(DocumentRetrievalScope scope, IDocumentRetrievalService retrievalService, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(retrievalService);
        ArgumentNullException.ThrowIfNull(logger);

        var invoker = new DocumentSearchInvoker(scope, retrievalService, logger);

        return AIFunctionFactory.Create(invoker.SearchAsync, ToolName, ToolDescription);
    }

    #endregion
}
