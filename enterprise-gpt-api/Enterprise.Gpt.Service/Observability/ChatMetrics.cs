using System.Diagnostics.Metrics;
using Enterprise.Gpt.Common.Observability;

namespace Enterprise.Gpt.Service.Observability;

/// <summary>
/// The instruments the chat pipeline and document ingestion record to.
/// </summary>
/// <remarks>
/// <para>
/// Named with <see cref="TelemetryNames.ChatSource"/> so the exporter registration that already
/// adds that meter picks these up with no wiring change — which is also why the ingestion instruments
/// ride it rather than a meter of their own that nothing would export. With no Application Insights
/// connection string configured the distro is skipped entirely and nothing is exported — the
/// instruments are still recorded to, which costs nothing and keeps the code path identical between
/// environments.
/// </para>
/// <para>
/// No dimension carries a conversation id, a user id, message content, or anything read out of an
/// uploaded file — a sheet name, a column name and a cell value are all file content. Metric
/// dimensions are high-cardinality storage that is retained and queried far more widely than logs,
/// and none of those would answer a question the deployment or document type does not.
/// </para>
/// </remarks>
public static class ChatMetrics
{
    private static readonly Meter Meter = new(TelemetryNames.ChatSource);

    /// <summary>
    /// The signed relative error between the estimated prompt and the provider's reported input
    /// tokens, as a fraction — 0.05 is a five percent overestimate.
    /// </summary>
    /// <remarks>
    /// Recorded only for turns with no tool calls, which is the only condition under which the two
    /// measure the same thing: a turn that ran tools was billed for prompts the estimator never
    /// saw.
    /// </remarks>
    private static readonly Histogram<double> EstimatorRelativeError = Meter.CreateHistogram<double>(
        "enterprise_gpt.chat.estimator.relative_error",
        unit: "1",
        description: "Signed relative error of the local token estimate against provider-reported input tokens, on turns with no tool calls.");

    /// <summary>
    /// Messages dropped from a turn's replayed history because they did not fit the model's context
    /// window.
    /// </summary>
    private static readonly Counter<long> BudgetDroppedMessages = Meter.CreateCounter<long>(
        "enterprise_gpt.chat.budget.dropped_messages",
        unit: "{message}",
        description: "Messages trimmed from replayed history to fit the model's context window.");

    /// <summary>
    /// Tokens held by the messages dropped from a turn's replayed history.
    /// </summary>
    private static readonly Counter<long> BudgetDroppedTokens = Meter.CreateCounter<long>(
        "enterprise_gpt.chat.budget.dropped_tokens",
        unit: "{token}",
        description: "Estimated tokens held by messages trimmed from replayed history.");

    /// <summary>
    /// How long a whole summarization run took, tagged by the path it took and how it ended.
    /// </summary>
    /// <remarks>
    /// A run, not a call: the map, collapse and reduce calls of one document roll into one
    /// measurement, which is the grain a caller waiting on the tool actually experiences.
    /// </remarks>
    private static readonly Histogram<double> SummaryRunDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.document_summary.run.duration",
        unit: "s",
        description: "Duration of a document summarization run, by path and outcome.");

    /// <summary>
    /// How many window-sized map units a run split its document into. Zero on the single-pass path.
    /// </summary>
    private static readonly Histogram<int> SummaryRunMapUnits = Meter.CreateHistogram<int>(
        "enterprise_gpt.document_summary.run.map_units",
        unit: "{unit}",
        description: "Map units one summarization run split its document into.");

    /// <summary>
    /// How long reading one spreadsheet's text and grid took, tagged by format and how it ended.
    /// </summary>
    private static readonly Histogram<double> SheetIngestionDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.sheet_ingestion.duration",
        unit: "s",
        description: "Duration of one spreadsheet extraction, by document type and outcome.");

    /// <summary>
    /// How many sheets one upload yielded. Zero on any outcome that produced none.
    /// </summary>
    private static readonly Histogram<int> SheetIngestionSheets = Meter.CreateHistogram<int>(
        "enterprise_gpt.sheet_ingestion.sheets",
        unit: "{sheet}",
        description: "Sheets read from one spreadsheet upload.");

    /// <summary>
    /// Data rows read across every sheet of one upload, headers excluded.
    /// </summary>
    private static readonly Histogram<int> SheetIngestionRows = Meter.CreateHistogram<int>(
        "enterprise_gpt.sheet_ingestion.rows",
        unit: "{row}",
        description: "Data rows read from one spreadsheet upload, across all of its sheets.");

    /// <summary>
    /// Columns of the widest sheet in one upload.
    /// </summary>
    /// <remarks>
    /// The widest rather than the total, because it is the one that approaches
    /// <c>Sheets:MaxColumnsPerSheet</c> and decides whether an upload is refused.
    /// </remarks>
    private static readonly Histogram<int> SheetIngestionColumns = Meter.CreateHistogram<int>(
        "enterprise_gpt.sheet_ingestion.columns",
        unit: "{column}",
        description: "Columns of the widest sheet in one spreadsheet upload.");

    /// <summary>
    /// How long one whole sheet-query invocation took, tagged by operation and how it ended.
    /// </summary>
    private static readonly Histogram<double> SheetQueryDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.sheet_query.duration",
        unit: "s",
        description: "Duration of one sheet query invocation, by operation and outcome.");

    /// <summary>
    /// How long a whole File Agent run took, tagged by how it ended.
    /// </summary>
    /// <remarks>
    /// A run, not a call: the pre-flight, every sandbox round trip, verification and the store roll
    /// into one measurement, which is the grain a user waiting on the tool actually experiences.
    /// </remarks>
    private static readonly Histogram<double> FileAgentRunDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.file_agent.run.duration",
        unit: "s",
        description: "Duration of one File Agent run, by outcome.");

    /// <summary>
    /// One increment per artifact re-opened and measured before it was stored.
    /// </summary>
    private static readonly Counter<long> FileAgentVerification = Meter.CreateCounter<long>(
        "enterprise_gpt.file_agent.verification",
        unit: "{artifact}",
        description: "Artifacts re-opened and checked before being stored, by outcome and format.");

    /// <summary>
    /// How long the sandbox leg of a run took, tagged by how it ended.
    /// </summary>
    /// <remarks>
    /// The model round trip that carries the code interpreter, which is the only observable proxy for
    /// billed session seconds on this route — the provider bills the session separately from tokens and
    /// reports neither back. Distinct from the run duration above, which also covers work on this host.
    /// </remarks>
    private static readonly Histogram<double> FileAgentSandboxDuration = Meter.CreateHistogram<double>(
        "enterprise_gpt.file_agent.sandbox.duration",
        unit: "s",
        description: "Duration of one sandbox leg of a File Agent run, by outcome.");

    /// <summary>
    /// Sandbox legs in flight right now, across every conversation this instance is serving.
    /// </summary>
    private static readonly UpDownCounter<long> FileAgentSandboxActive = Meter.CreateUpDownCounter<long>(
        "enterprise_gpt.file_agent.sandbox.active",
        unit: "{session}",
        description: "Sandbox calls currently in flight across all conversations.");

    /// <summary>
    /// Records how far the local estimate was from what the provider reported.
    /// </summary>
    /// <param name="providerId">The provider that served the turn.</param>
    /// <param name="deploymentName">The deployment that served the turn.</param>
    /// <param name="estimatedPromptTokens">The estimated prompt, including per-turn overhead.</param>
    /// <param name="reportedInputTokens">The provider's reported input tokens. Must be positive.</param>
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

    /// <summary>
    /// Records what a turn's context budget dropped.
    /// </summary>
    /// <param name="deploymentName">The deployment the turn was sent to.</param>
    /// <param name="droppedMessages">How many messages were trimmed.</param>
    /// <param name="droppedTokens">How many estimated tokens they held.</param>
    /// <remarks>
    /// A no-op when nothing was dropped, so the counters measure trimming rather than traffic.
    /// </remarks>
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

    /// <summary>
    /// Records a completed or failed summarization run.
    /// </summary>
    /// <param name="deploymentName">The summarizer deployment that served the run.</param>
    /// <param name="path">Which route the run took: <c>cache-hit</c>, <c>single-pass</c>, <c>map-reduce</c> or <c>digest</c>.</param>
    /// <param name="outcome">How it ended: <c>success</c>, <c>failure</c> or <c>cancelled</c>.</param>
    /// <param name="elapsed">Wall-clock duration of the whole run.</param>
    /// <param name="mapUnits">Map units the run produced, or <see langword="null"/> when it never split.</param>
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

    /// <summary>
    /// Records what one spreadsheet extraction read and what it cost.
    /// </summary>
    /// <param name="documentType">The uploaded format, as the extension without its dot.</param>
    /// <param name="outcome">How it ended: <c>success</c>, <c>refused</c>, <c>error</c> or <c>cancelled</c>.</param>
    /// <param name="sheetCount">Sheets read.</param>
    /// <param name="rowCount">Data rows read across every sheet.</param>
    /// <param name="columnCount">Columns of the widest sheet read.</param>
    /// <param name="elapsed">Wall-clock duration of the extraction.</param>
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

    /// <summary>
    /// Records a completed, refused or failed sheet query.
    /// </summary>
    /// <param name="operation">The parsed operation, or <c>unknown</c> when the request named none.</param>
    /// <param name="outcome">How it ended: <c>success</c>, <c>refused</c>, <c>timed-out</c>, <c>error</c> or <c>cancelled</c>.</param>
    /// <param name="elapsed">Wall-clock duration of the whole invocation.</param>
    /// <remarks>
    /// The operation must be the parsed one: the result echoes the model's own text back unvalidated,
    /// and using that would make an unbounded model-supplied string a metric dimension.
    /// </remarks>
    public static void RecordSheetQuery(string operation, string outcome, TimeSpan elapsed) =>
        SheetQueryDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("sheet_query.operation", operation),
            new KeyValuePair<string, object?>("sheet_query.outcome", outcome));

    /// <summary>
    /// Records a completed, refused or failed File Agent run.
    /// </summary>
    /// <param name="outcome">One of <see cref="Agents.FileAgentOutcomes"/>.</param>
    /// <param name="elapsed">Wall-clock duration of the whole run.</param>
    public static void RecordFileAgentRun(string outcome, TimeSpan elapsed) =>
        FileAgentRunDuration.Record(
            elapsed.TotalSeconds, new KeyValuePair<string, object?>("file_agent.outcome", outcome));

    /// <summary>
    /// Records one sandbox leg of a File Agent run.
    /// </summary>
    /// <param name="outcome">Either <c>success</c> or how the leg failed.</param>
    /// <param name="elapsed">Wall-clock duration of the leg.</param>
    public static void RecordFileAgentSandbox(string outcome, TimeSpan elapsed) =>
        FileAgentSandboxDuration.Record(
            elapsed.TotalSeconds, new KeyValuePair<string, object?>("file_agent.outcome", outcome));

    /// <summary>
    /// Moves the count of sandbox legs in flight.
    /// </summary>
    /// <param name="delta"><c>1</c> when a leg starts, <c>-1</c> when it ends.</param>
    public static void RecordFileAgentSandboxActive(int delta) => FileAgentSandboxActive.Add(delta);

    /// <summary>
    /// Records one artifact re-opened and measured.
    /// </summary>
    /// <param name="documentType">The produced format, as the extension without its dot.</param>
    /// <param name="passed">Whether it opened and matched the shape it claimed.</param>
    /// <remarks>
    /// The direct source for the artifact-validity target, which is why the format is a dimension and
    /// the file's own name is not: a name is user content, and the question the metric answers is which
    /// formats fail.
    /// </remarks>
    public static void RecordFileAgentVerification(string documentType, bool passed) =>
        FileAgentVerification.Add(
            1,
            new KeyValuePair<string, object?>("file_agent.outcome", passed ? "passed" : "failed"),
            new KeyValuePair<string, object?>("document.type", documentType));
}
