namespace Enterprise.Gpt.Common.Observability;

/// <summary>
/// The telemetry source names this application produces and exports under.
/// </summary>
/// <remarks>
/// In <c>Common</c> rather than beside the exporter registration because both ends need it and they
/// sit on opposite sides of the layering: the API registers the name with the OpenTelemetry
/// pipeline, while the services that emit metrics are underneath it and cannot reference the API.
/// Naming it once is what stops the producer and the consumer drifting apart — a metric emitted
/// under a name nothing registered is silently never exported.
/// </remarks>
public static class TelemetryNames
{
    /// <summary>
    /// The name the chat pipeline emits traces and metrics under.
    /// </summary>
    /// <remarks>
    /// Used for both an <c>ActivitySource</c> and a <c>Meter</c>, because the
    /// Microsoft.Extensions.AI chat clients build both from the single source name they are given.
    /// </remarks>
    public const string ChatSource = "Enterprise.Gpt.Chat";
}
