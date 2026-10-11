namespace Enterprise.Gpt.Common.Constants;

/// <summary>
/// The telemetry source names this application produces and exports under.
/// </summary>
/// <remarks>
/// In <c>Common</c> so the API exporter and the emitting services share one name; a metric emitted under
/// a name nothing registered is silently never exported.
/// </remarks>
public static class TelemetryNames
{
    /// <summary>
    /// The name the chat pipeline emits traces and metrics under.
    /// </summary>
    /// <remarks>
    /// Microsoft.Extensions.AI chat clients build both an <c>ActivitySource</c> and a <c>Meter</c> from it.
    /// </remarks>
    public const string ChatSource = "Enterprise.Gpt.Chat";
}
