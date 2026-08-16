using System.Diagnostics.Metrics;
using Enterprise.Gpt.Common.Observability;

namespace Enterprise.Gpt.Service.Observability;

/// <summary>
/// The instruments the chat pipeline records to.
/// </summary>
/// <remarks>
/// <para>
/// Named with <see cref="TelemetryNames.ChatSource"/> so the exporter registration that already
/// adds that meter picks these up with no wiring change. With no Application Insights connection
/// string configured the distro is skipped entirely and nothing is exported — the instruments are
/// still recorded to, which costs nothing and keeps the code path identical between environments.
/// </para>
/// <para>
/// No dimension carries a conversation id, a user id, or message content. Metric dimensions are
/// high-cardinality storage that is retained and queried far more widely than logs, and neither
/// identifier would answer a question the deployment name does not.
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
}
