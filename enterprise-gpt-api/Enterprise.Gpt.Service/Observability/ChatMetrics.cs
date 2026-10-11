using System.Diagnostics.Metrics;
using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Service.Observability;

/// <summary>The instruments the chat pipeline and document ingestion record to.</summary>
/// <remarks>
/// On the <see cref="TelemetryNames.ChatSource"/> meter so the existing exporter registration picks them up.
/// No dimension carries a conversation or user id, message content or file content: metrics are retained
/// and queried widely.
/// </remarks>
public static class ChatMetrics
{
    private static readonly Meter Meter = new(TelemetryNames.ChatSource);

    // Positive is an overestimate (0.05 = 5%). Tool-free turns only: a turn that ran tools was billed for
    // prompts the estimator never saw.
    private static readonly Histogram<double> EstimatorRelativeError = Meter.CreateHistogram<double>(
        "enterprise_gpt.chat.estimator.relative_error",
        unit: "1",
        description: "Signed relative error of the local token estimate against provider-reported input tokens, on turns with no tool calls.");

    private static readonly Counter<long> BudgetDroppedMessages = Meter.CreateCounter<long>(
        "enterprise_gpt.chat.budget.dropped_messages",
        unit: "{message}",
        description: "Messages trimmed from replayed history to fit the model's context window.");

    private static readonly Counter<long> BudgetDroppedTokens = Meter.CreateCounter<long>(
        "enterprise_gpt.chat.budget.dropped_tokens",
        unit: "{token}",
        description: "Estimated tokens held by messages trimmed from replayed history.");

    // Per run, not per call: map, collapse and reduce roll into the one wait the caller experiences.
    private static readonly Histogram<double> SummaryRunDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.document_summary.run.duration",
        unit: "s",
        description: "Duration of a document summarization run, by path and outcome.");

    // Zero on the single-pass path.
    private static readonly Histogram<int> SummaryRunMapUnits = Meter.CreateHistogram<int>(
        "enterprise_gpt.document_summary.run.map_units",
        unit: "{unit}",
        description: "Map units one summarization run split its document into.");

    private static readonly Histogram<double> SheetIngestionDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.sheet_ingestion.duration",
        unit: "s",
        description: "Duration of one spreadsheet extraction, by document type and outcome.");

    private static readonly Histogram<int> SheetIngestionSheets = Meter.CreateHistogram<int>(
        "enterprise_gpt.sheet_ingestion.sheets",
        unit: "{sheet}",
        description: "Sheets read from one spreadsheet upload.");

    private static readonly Histogram<int> SheetIngestionRows = Meter.CreateHistogram<int>(
        "enterprise_gpt.sheet_ingestion.rows",
        unit: "{row}",
        description: "Data rows read from one spreadsheet upload, across all of its sheets.");

    // The widest, not the total: it is what approaches Sheets:MaxColumnsPerSheet and decides a refusal.
    private static readonly Histogram<int> SheetIngestionColumns = Meter.CreateHistogram<int>(
        "enterprise_gpt.sheet_ingestion.columns",
        unit: "{column}",
        description: "Columns of the widest sheet in one spreadsheet upload.");

    private static readonly Histogram<double> SheetQueryDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.sheet_query.duration",
        unit: "s",
        description: "Duration of one sheet query invocation, by operation and outcome.");

    // Per run, not per call: pre-flight, sandbox round trips, verification and the store are the one wait
    // the user experiences.
    private static readonly Histogram<double> FileAgentRunDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.file_agent.run.duration",
        unit: "s",
        description: "Duration of one File Agent run, by outcome.");

    private static readonly Counter<long> FileAgentVerification = Meter.CreateCounter<long>(
        "enterprise_gpt.file_agent.verification",
        unit: "{artifact}",
        description: "Artifacts re-opened and checked before being stored, by outcome and format.");

    // The code-interpreter round trip: the only observable proxy for billed session seconds, which the
    // provider bills apart from tokens and never reports.
    private static readonly Histogram<double> FileAgentSandboxDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.file_agent.sandbox.duration",
        unit: "s",
        description: "Duration of one sandbox leg of a File Agent run, by outcome.");

    private static readonly UpDownCounter<long> FileAgentSandboxActive = Meter.CreateUpDownCounter<long>(
        "enterprise_gpt.file_agent.sandbox.active",
        unit: "{session}",
        description: "Sandbox calls currently in flight across all conversations.");

    #region Public static methods

    /// <summary>Records the local estimate's error; pass the estimate including per-turn overhead.</summary>
    public static void RecordEstimatorError(
        Guid providerId, string? deploymentName, long estimatedPromptTokens, long reportedInputTokens)
    {
        if (reportedInputTokens <= 0)
        {
            return;
        }

        var error = (estimatedPromptTokens - (double)reportedInputTokens) / reportedInputTokens;

        EstimatorRelativeError.Record(
            error,
            new KeyValuePair<string, object?>("provider.id", providerId),
            new KeyValuePair<string, object?>("deployment.name", deploymentName ?? "unknown"));
    }

    /// <summary>Records what a turn's context budget dropped; a no-op when nothing was.</summary>
    public static void RecordBudgetTrim(string? deploymentName, int droppedMessages, long droppedTokens)
    {
        if (droppedMessages <= 0)
        {
            return;
        }

        var deployment = new KeyValuePair<string, object?>("deployment.name", deploymentName ?? "unknown");

        BudgetDroppedMessages.Add(droppedMessages, deployment);
        BudgetDroppedTokens.Add(droppedTokens, deployment);
    }

    /// <summary>Records a summarization run.</summary>
    /// <param name="deploymentName">The summarizer deployment.</param>
    /// <param name="path"><c>cache-hit</c>, <c>single-pass</c>, <c>map-reduce</c> or <c>digest</c>.</param>
    /// <param name="outcome"><c>success</c>, <c>truncated</c>, <c>failure</c> or <c>cancelled</c>.</param>
    /// <param name="elapsed">The whole run.</param>
    /// <param name="mapUnits"><see langword="null"/> when the run never split.</param>
    public static void RecordSummaryRun(
        string? deploymentName, string path, string outcome, TimeSpan elapsed, int? mapUnits)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("deployment.name", deploymentName ?? "unknown"),
            new("summary.path", path),
            new("summary.outcome", outcome)
        ];

        SummaryRunDuration.Record(elapsed.TotalSeconds, tags);

        if (mapUnits is int units)
        {
            SummaryRunMapUnits.Record(units, tags);
        }
    }

    /// <summary>Records one spreadsheet extraction.</summary>
    /// <param name="documentType">The extension without its dot.</param>
    /// <param name="outcome"><c>success</c>, <c>refused</c>, <c>error</c> or <c>cancelled</c>.</param>
    /// <param name="sheetCount">Sheets read.</param>
    /// <param name="rowCount">Data rows across every sheet, headers excluded.</param>
    /// <param name="columnCount">Columns of the widest sheet.</param>
    /// <param name="elapsed">The whole extraction.</param>
    public static void RecordSheetIngestion(
        string documentType, string outcome, int sheetCount, int rowCount, int columnCount, TimeSpan elapsed)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("document.type", documentType),
            new("sheet.outcome", outcome)
        ];

        SheetIngestionDuration.Record(elapsed.TotalSeconds, tags);
        SheetIngestionSheets.Record(sheetCount, tags);
        SheetIngestionRows.Record(rowCount, tags);
        SheetIngestionColumns.Record(columnCount, tags);
    }

    /// <summary>Records a sheet query invocation.</summary>
    /// <param name="operation">
    /// The parsed operation or <c>unknown</c>; never the model's echoed text, an unbounded dimension.
    /// </param>
    /// <param name="outcome"><c>success</c>, <c>refused</c>, <c>timed-out</c>, <c>error</c> or <c>cancelled</c>.</param>
    /// <param name="elapsed">The whole invocation.</param>
    public static void RecordSheetQuery(string operation, string outcome, TimeSpan elapsed) =>
        SheetQueryDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("sheet_query.operation", operation),
            new KeyValuePair<string, object?>("sheet_query.outcome", outcome));

    /// <summary>Records a File Agent run.</summary>
    /// <param name="outcome">One of <see cref="Agents.FileAgentOutcomes"/>.</param>
    /// <param name="elapsed">The whole run.</param>
    public static void RecordFileAgentRun(string outcome, TimeSpan elapsed) =>
        FileAgentRunDuration.Record(
            elapsed.TotalSeconds, new KeyValuePair<string, object?>("file_agent.outcome", outcome));

    /// <summary>Records one sandbox leg of a File Agent run.</summary>
    /// <param name="outcome">Either <c>success</c> or how the leg failed.</param>
    /// <param name="elapsed">The leg.</param>
    public static void RecordFileAgentSandbox(string outcome, TimeSpan elapsed) =>
        FileAgentSandboxDuration.Record(
            elapsed.TotalSeconds, new KeyValuePair<string, object?>("file_agent.outcome", outcome));

    /// <param name="delta"><c>1</c> when a sandbox leg starts, <c>-1</c> when it ends.</param>
    public static void RecordFileAgentSandboxActive(int delta) => FileAgentSandboxActive.Add(delta);

    /// <summary>Records one verified artifact, by format only: a file name is user content.</summary>
    /// <param name="documentType">The extension without its dot.</param>
    /// <param name="passed">Whether it opened and matched the shape it claimed.</param>
    public static void RecordFileAgentVerification(string documentType, bool passed) =>
        FileAgentVerification.Add(
            1,
            new KeyValuePair<string, object?>("file_agent.outcome", passed ? "passed" : "failed"),
            new KeyValuePair<string, object?>("document.type", documentType));

    #endregion
}
