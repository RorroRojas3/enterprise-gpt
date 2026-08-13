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
/// </remarks>
/// <param name="meterFactory">Creates the meter, so the host controls its lifetime.</param>
public sealed class ChatMetrics(IMeterFactory meterFactory) : IDisposable
{
    /// <summary>
    /// The meter both this type and the chat pipeline's OpenTelemetry instrumentation publish on.
    /// </summary>
    public const string MeterName = "Enterprise.Gpt.Chat";

    private readonly Meter _meter = meterFactory.Create(MeterName);

    private Histogram<double>? _estimateRelativeError;
    private Counter<long>? _droppedMessages;
    private Counter<long>? _droppedTokens;

    private Histogram<double> EstimateRelativeError => _estimateRelativeError ??= _meter.CreateHistogram<double>(
        "enterprisegpt.chat.token_estimate.relative_error",
        unit: "1",
        description: "Signed relative error of the estimated prompt against the provider's reported input tokens, on turns where no tool ran.");

    private Counter<long> DroppedMessages => _droppedMessages ??= _meter.CreateCounter<long>(
        "enterprisegpt.chat.context_budget.dropped_messages",
        unit: "{message}",
        description: "Messages dropped from a turn's replayed history to fit the model's context window.");

    private Counter<long> DroppedTokens => _droppedTokens ??= _meter.CreateCounter<long>(
        "enterprisegpt.chat.context_budget.dropped_tokens",
        unit: "{token}",
        description: "Tokens held by the messages dropped from a turn's replayed history.");

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
        EstimateRelativeError.Record(
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

        DroppedMessages.Add(droppedMessages, tag);
        DroppedTokens.Add(droppedTokens, tag);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _meter.Dispose();
    }
}
