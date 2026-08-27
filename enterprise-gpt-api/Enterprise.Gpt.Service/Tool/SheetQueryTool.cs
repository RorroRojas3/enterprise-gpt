using Andes.Extensions.AI;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// Exposes the deterministic spreadsheet lookup to the assistant as a callable function.
/// </summary>
/// <remarks>
/// Built per turn and closed over the corpus resolved for that turn, so a query can only ever reach a
/// sheet the caller was already entitled to read. The model names a sheet, a column, an operation from a
/// closed set and literal comparison values, and nothing else.
/// </remarks>
public static class SheetQueryTool
{
    /// <summary>
    /// The name the model calls this tool by, and the name it appears under in the activity feed and the
    /// usage audit.
    /// </summary>
    /// <remarks>
    /// Chosen the same way <see cref="DocumentTool.ToolName"/> was: tool-call usage is attributed to an
    /// MCP server by matching the longest <c>{server}_</c> prefix, so a name such as <c>query_sheet</c>
    /// would be credited to a server called "query" if one ever existed.
    /// </remarks>
    public const string ToolName = "sheet_query";

    /// <summary>
    /// The error number SQL Server reports for a command timeout.
    /// </summary>
    private const int SqlTimeoutError = -2;

    private const string ToolDescription =
        "Computes an exact answer from the rows of an attached spreadsheet: a sum, average, minimum, maximum or "
            + "count over one column, optionally broken down by another, or the rows matching a set of conditions. "
            + "Use it for anything arithmetic or conditional — totals, averages, counts, rankings, \"how many\", "
            + "\"which rows\" — where " + DocumentTool.ToolName + " can only quote passages. Call it with no sheet "
            + "name first to see which sheets and columns exist.";

    /// <summary>
    /// Builds the sheet-query function for one turn.
    /// </summary>
    /// <param name="scope">The corpus this turn may query, from <see cref="IDocumentRetrievalService.GetScopeAsync"/>.</param>
    /// <param name="sheetQueryService">The service backing the tool.</param>
    /// <param name="toolTimeoutSeconds">How long one whole invocation may run before it is abandoned.</param>
    /// <param name="logger">Logger for failures that must not reach the model verbatim.</param>
    /// <returns>The function to attach to <see cref="ChatOptions.Tools"/>.</returns>
    /// <exception cref="ArgumentNullException">Any reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toolTimeoutSeconds"/> is not positive.</exception>
    public static AIFunction Create(
        DocumentRetrievalScope scope,
        ISheetQueryService sheetQueryService,
        int toolTimeoutSeconds,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sheetQueryService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toolTimeoutSeconds);

        var invoker = new SheetQueryInvoker(scope, sheetQueryService, toolTimeoutSeconds, logger);

        return AIFunctionFactory.Create(invoker.QueryAsync, ToolName, ToolDescription);
    }

    /// <summary>
    /// Counts the spreadsheets in a scope, for a progress message that names no file.
    /// </summary>
    internal static int CountSpreadsheets(DocumentRetrievalScope scope) => scope.Documents
        .Count(d => DocumentRetrievalService.SpreadsheetExtensions.Contains(Path.GetExtension(d.Name)));

    /// <summary>
    /// Holds the per-turn state the function body needs, so the state is captured once rather than
    /// resolved on every call.
    /// </summary>
    private sealed class SheetQueryInvoker(
        DocumentRetrievalScope scope,
        ISheetQueryService sheetQueryService,
        int toolTimeoutSeconds,
        ILogger logger)
    {
        private readonly DocumentRetrievalScope _scope = scope;
        private readonly ISheetQueryService _sheetQueryService = sheetQueryService;
        private readonly int _toolTimeoutSeconds = toolTimeoutSeconds;
        private readonly ILogger _logger = logger;
        private readonly int _spreadsheetCount = CountSpreadsheets(scope);

        /// <summary>
        /// Computes an aggregate over one sheet, or lists the rows matching a set of conditions.
        /// </summary>
        /// <remarks>
        /// Progress is reported through the ambient <see cref="ChatProgress"/> reporter, which is the only
        /// channel that reaches the activity feed: tool arguments and results are deliberately never
        /// streamed, so neither a filter value nor a computed figure may appear in a progress message.
        /// </remarks>
        public async Task<SheetQueryResult> QueryAsync(
            [Description("What to compute: sum, avg, min, max, count, or filter. Use filter to list the matching rows instead of computing a value.")]
            string operation,
            [Description("The sheet to query, by name. Omit it to use the only attached sheet, or to be told which sheets and columns exist.")]
            string? sheetName = null,
            [Description("The column to compute over. Required for sum, avg, min and max; optional for count; not used by filter.")]
            string? column = null,
            [Description("Optional column to break the result down by. One column only.")]
            string? groupBy = null,
            [Description("Optional conditions the rows must meet. Every one of them must hold.")]
            SheetFilter[]? filters = null,
            CancellationToken cancellationToken = default)
        {
            ChatProgress.Report(_spreadsheetCount == 1
                ? "Reading 1 spreadsheet…"
                : $"Reading {_spreadsheetCount} spreadsheets…");

            // Bounds the whole invocation, where SheetQueryOptions.TimeoutSeconds bounds one statement
            // inside it. Linked, so the turn being torn down still cancels promptly.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(_toolTimeoutSeconds));

            try
            {
                var result = await _sheetQueryService
                    .QueryAsync(_scope, operation, sheetName, column, groupBy, filters, deadline.Token)
                    .ConfigureAwait(false);

                ChatProgress.Report(result switch
                {
                    { Rows: { } rows } => rows.Count == 1 ? "1 matching row" : $"{rows.Count} matching rows",
                    { Groups: { } groups } => groups.Count == 1 ? "1 group" : $"{groups.Count} groups",
                    { Value: not null } => "Computed a result",
                    _ => "Nothing to compute"
                });

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The turn is being torn down; let cancellation stay cancellation rather than turning it
                // into a tool error the model would try to work around.
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SqlException { Number: SqlTimeoutError })
            {
                // Either the deadline above or the statement's own command timeout, whichever came first.
                // Reported to the model rather than thrown at the turn, because a query that took too long
                // is something the user can be told.
                _logger.LogWarning(
                    ex,
                    "A sheet query for conversation {ConversationId} ran past its deadline of {TimeoutSeconds}s.",
                    _scope.ConversationId, _toolTimeoutSeconds);

                throw new InvalidOperationException(
                    "Reading the spreadsheet took too long and was stopped. Tell the user the sheet was too large "
                        + "for this question, and offer to answer a narrower one.");
            }
            catch (Exception ex)
            {
                // Function invocation is configured with detailed errors, so whatever is thrown here is
                // handed to the model verbatim. The real exception goes to the log; the model gets a
                // sentence it can act on and nothing about the database behind it.
                _logger.LogError(ex, "The sheet query failed for conversation {ConversationId}.", _scope.ConversationId);

                throw new InvalidOperationException(
                    "The spreadsheet could not be read. Answer from the conversation so far, and tell the user their "
                        + "spreadsheet could not be queried.");
            }
        }
    }
}
