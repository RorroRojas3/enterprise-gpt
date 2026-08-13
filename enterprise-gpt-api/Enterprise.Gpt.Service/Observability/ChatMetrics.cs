using System.Diagnostics.Metrics;

namespace Enterprise.Gpt.Service.Observability;

/// <summary>
/// The application's own chat instruments, published on the same meter the chat pipeline's
/// library-emitted metrics use.
/// </summary>
/// <remarks>
/// <para>
/// Lives here rather than beside the telemetry registration because the code that records is in
/// this assembly and cannot reference the API's. Sharing <see cref="MeterName"/> is what makes
/// these export through the meter the host already registers, with no second registration.
/// </para>
/// <para>
/// No dimension carries a conversation identifier, a user identifier or message content: a metric
/// is retained and queried far more widely than a log, and per-conversation cardinality would make
/// it both expensive and disclosing.
/// </para>
/// <para>
/// Not disposable: <see cref="IMeterFactory"/> owns the lifetime of every meter it creates, so
/// disposing one here would have no effect.
/// </para>
/// </remarks>
public sealed class ChatMetrics
{
    /// <summary>
    /// The meter both this type and the chat pipeline's OpenTelemetry instrumentation publish on.
    /// </summary>
    public const string MeterName = "Enterprise.Gpt.Chat";

    private readonly Histogram<double> _estimateRelativeError;
    private readonly Counter<long> _droppedMessages;
    private readonly Counter<long> _droppedTokens;

    /// <summary>
    /// Creates the instruments on the shared chat meter.
    /// </summary>
    /// <param name="meterFactory">Creates the meter, and owns its lifetime.</param>
    /// <remarks>
    /// Instruments are built here rather than on first use: three of them on a singleton cost
    /// nothing, where a lazily initialized field raced by two threads publishes the same instrument
    /// twice, which the OpenTelemetry SDK reports as a duplicate-instrument conflict.
    /// </remarks>
    public ChatMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _estimateRelativeError = meter.CreateHistogram<double>(
            "enterprisegpt.chat.token_estimate.relative_error",
            unit: "1",
            description: "Signed relative error of the estimated prompt against the provider's reported input tokens, on turns where no tool ran.");

        _droppedMessages = meter.CreateCounter<long>(
            "enterprisegpt.chat.context_budget.dropped_messages",
            unit: "{message}",
            description: "Messages dropped from a turn's replayed history to fit the model's context window.");

        _droppedTokens = meter.CreateCounter<long>(
            "enterprisegpt.chat.context_budget.dropped_tokens",
            unit: "{token}",
            description: "Tokens held by the messages dropped from a turn's replayed history.");
    }

    /// <summary>
    /// Records how far the estimate was from what the provider reported.
    /// </summary>
    /// <param name="signedRelativeError">
    /// <c>(estimate - reported) / reported</c>. Signed, so a systematic undercount is
    /// distinguishable from noise around zero rather than averaging away with it.
    /// </param>
    /// <param name="providerId">The provider that reported the figure.</param>
    /// <param name="deploymentName">The deployment that served the turn.</param>
    public void RecordEstimateRelativeError(double signedRelativeError, Guid providerId, string deploymentName)
    {
        _estimateRelativeError.Record(
            signedRelativeError,
            new KeyValuePair<string, object?>("provider.id", providerId.ToString()),
            new KeyValuePair<string, object?>("deployment.name", deploymentName));
    }

    /// <summary>
    /// Records what a turn's budget dropped.
    /// </summary>
    /// <param name="droppedMessages">How many messages were dropped.</param>
    /// <param name="droppedTokens">What those messages held.</param>
    /// <param name="deploymentName">The deployment whose window forced the trim.</param>
    public void RecordBudgetDrop(int droppedMessages, long droppedTokens, string deploymentName)
    {
        var tag = new KeyValuePair<string, object?>("deployment.name", deploymentName);

        _droppedMessages.Add(droppedMessages, tag);
        _droppedTokens.Add(droppedTokens, tag);
    }
}
