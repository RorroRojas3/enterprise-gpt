using System.ComponentModel;
using System.Diagnostics;
using Andes.Extensions.AI;
using Enterprise.Gpt.Service.Documents.Retrieval;
using Enterprise.Gpt.Service.Documents.Retrieval.Models;
using Enterprise.Gpt.Service.Documents.Sheets.Models;
using Enterprise.Gpt.Service.Observability;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Documents.Sheets;

/// <summary>
/// Exposes the deterministic spreadsheet lookup to the assistant as a callable function.
/// </summary>
/// <remarks>
/// Built per turn over that turn's resolved corpus, so a query only reaches sheets the caller can
/// already read.
/// </remarks>
public static partial class SheetQueryTool
{
    /// <summary>The name the model, the activity feed and the usage audit know this tool by.</summary>
    /// <remarks>
    /// Chosen like <see cref="DocumentTool.ToolName"/>: usage is attributed to an MCP server by the longest
    /// <c>{server}_</c> prefix, so <c>query_sheet</c> would be credited to a server named "query".
    /// </remarks>
    public const string ToolName = "sheet_query";

    /// <summary>The error number SQL Server reports for a command timeout.</summary>
    private const int SqlTimeoutError = -2;

    private const string ToolDescription =
        "Computes an exact answer from the rows of an attached spreadsheet: a sum, average, minimum, maximum or "
            + "count over one column, optionally broken down by another, or the rows matching a set of conditions. "
            + "Use it for anything arithmetic or conditional — totals, averages, counts, rankings, \"how many\", "
            + "\"which rows\" — where " + DocumentTool.ToolName + " can only quote passages. Call it with no sheet "
            + "name first to see which sheets and columns exist.";

    private sealed partial class SheetQueryInvoker(
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

        /// <remarks>
        /// Tool arguments and results are deliberately never streamed, so no filter value or computed figure
        /// may appear in a <see cref="ChatProgress"/> message.
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

            // Bounds the whole invocation; SheetQueryOptions.TimeoutSeconds bounds each statement in it.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(_toolTimeoutSeconds));

            // Parsed, not the model's echoed text, which would put an unbounded value on a metric dimension.
            var measured = SheetQueryService.TryParseOperation(operation, out var parsed)
                ? parsed.ToString().ToLowerInvariant()
                : "unknown";

            var started = Stopwatch.GetTimestamp();

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

                // A refusal never throws, and a note does not mark one: queries that ran carry notes too.
                ChatMetrics.RecordSheetQuery(
                    measured,
                    result.IsRefusal ? "refused" : "success",
                    Stopwatch.GetElapsedTime(started));

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ChatMetrics.RecordSheetQuery(measured, outcome: "cancelled", Stopwatch.GetElapsedTime(started));

                // The turn is being torn down; a tool error would only invite the model to work around it.
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SqlException { Number: SqlTimeoutError })
            {
                ChatMetrics.RecordSheetQuery(measured, outcome: "timed-out", Stopwatch.GetElapsedTime(started));

                // Tool deadline or command timeout: reported to the model rather than failing the turn,
                // because a query that took too long is something the user can be told.
                LogSheetQueryTimedOut(_logger, ex, _scope.ConversationId, _toolTimeoutSeconds);

                throw new InvalidOperationException(
                    "Reading the spreadsheet took too long and was stopped. Tell the user the sheet was too large "
                        + "for this question, and offer to answer a narrower one.");
            }
            catch (Exception ex)
            {
                ChatMetrics.RecordSheetQuery(measured, outcome: "error", Stopwatch.GetElapsedTime(started));

                // Detailed errors are on, so whatever is thrown reaches the model verbatim: log the real
                // exception and give the model a sentence with nothing about the database.
                LogSheetQueryFailed(_logger, ex, _scope.ConversationId);

                throw new InvalidOperationException(
                    "The spreadsheet could not be read. Answer from the conversation so far, and tell the user their "
                        + "spreadsheet could not be queried.");
            }
        }

        #region Logging

        [LoggerMessage(Level = LogLevel.Warning, Message = "A sheet query for conversation {ConversationId} ran past its deadline of {TimeoutSeconds}s.")]
        private static partial void LogSheetQueryTimedOut(
            ILogger logger, Exception exception, Guid conversationId, int timeoutSeconds);

        [LoggerMessage(Level = LogLevel.Error, Message = "The sheet query failed for conversation {ConversationId}.")]
        private static partial void LogSheetQueryFailed(ILogger logger, Exception exception, Guid conversationId);

        #endregion
    }

    #region Public static methods

    /// <summary>
    /// Builds the sheet-query function for one turn.
    /// </summary>
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

    internal static int CountSpreadsheets(DocumentRetrievalScope scope) => scope.Documents
        .Count(d => DocumentRetrievalService.SpreadsheetExtensions.Contains(Path.GetExtension(d.Name)));

    #endregion
}
