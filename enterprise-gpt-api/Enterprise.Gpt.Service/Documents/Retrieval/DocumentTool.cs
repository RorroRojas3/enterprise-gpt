using Andes.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// Exposes retrieval over a conversation's documents to the assistant as a callable function.
/// </summary>
/// <remarks>
/// <para>
/// The tool is built per turn and closes over the corpus resolved for that turn, so a search can only
/// ever reach the documents the caller was already entitled to read. Nothing about the conversation or
/// the user is taken from the tool's arguments, which means a model that hallucinates an argument
/// cannot widen what it sees.
/// </para>
/// <para>
/// It is attached only when the conversation actually has documents. A tool that is always present and
/// always returns nothing teaches the model to stop calling it.
/// </para>
/// </remarks>
public static class DocumentTool
{
    /// <summary>
    /// The name the model calls this tool by, and the name it appears under in the activity feed and
    /// the usage audit.
    /// </summary>
    /// <remarks>
    /// Chosen not to begin with a word that could be an MCP server name. Tool-call usage is attributed
    /// to a server by matching the longest <c>{server}_</c> prefix, so a name such as
    /// <c>search_documents</c> would be credited to a server called "search" if one ever existed.
    /// </remarks>
    public const string ToolName = "document_search";

    private const string ToolDescription =
        "Searches the documents attached to this conversation and returns the passages most relevant to a query. " +
        "Use it whenever the user asks about their uploaded files, or about anything the documents might cover, " +
        "before answering from your own knowledge. Search more than once with different wording if the first " +
        "attempt returns nothing useful. Every passage comes back with a citation to quote.";

    /// <summary>
    /// Builds the retrieval function for one turn.
    /// </summary>
    /// <param name="scope">The corpus this turn may search, from <see cref="IDocumentRetrievalService.GetScopeAsync"/>.</param>
    /// <param name="retrievalService">The retrieval service backing the tool.</param>
    /// <param name="logger">Logger for failures that must not reach the model verbatim.</param>
    /// <returns>The function to attach to <see cref="ChatOptions.Tools"/>.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static AIFunction Create(DocumentRetrievalScope scope, IDocumentRetrievalService retrievalService, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(retrievalService);
        ArgumentNullException.ThrowIfNull(logger);

        var invoker = new DocumentSearchInvoker(scope, retrievalService, logger);

        return AIFunctionFactory.Create(invoker.SearchAsync, ToolName, ToolDescription);
    }

    /// <summary>
    /// Holds the per-turn state the function body needs, so the state is captured once rather than
    /// resolved on every call.
    /// </summary>
    private sealed class DocumentSearchInvoker(
        DocumentRetrievalScope scope,
        IDocumentRetrievalService retrievalService,
        ILogger logger)
    {
        private readonly DocumentRetrievalScope _scope = scope;
        private readonly IDocumentRetrievalService _retrievalService = retrievalService;
        private readonly ILogger _logger = logger;

        /// <summary>
        /// Searches the conversation's documents.
        /// </summary>
        /// <remarks>
        /// Progress is reported through the ambient <see cref="ChatProgress"/> reporter, which is the only
        /// channel that reaches the activity feed: tool arguments and results are deliberately never
        /// streamed, so the query itself must not be put in a progress message.
        /// </remarks>
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
                // The turn is being torn down; let cancellation stay cancellation rather than turning it
                // into a tool error the model would try to work around.
                throw;
            }
            catch (Exception ex)
            {
                // Function invocation is configured with detailed errors, so whatever is thrown here is
                // handed to the model verbatim. The real exception goes to the log; the model gets a
                // sentence it can act on and nothing about the database behind it.
                _logger.LogError(ex, "Document search failed for conversation {ConversationId}.", _scope.ConversationId);

                throw new InvalidOperationException("The document search could not be completed. Answer from the conversation so far, and tell the user their documents could not be searched.");
            }
        }
    }
}
