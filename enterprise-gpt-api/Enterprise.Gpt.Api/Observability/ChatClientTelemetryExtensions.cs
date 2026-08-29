using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Api.Observability;

/// <summary>
/// The telemetry step every chat-client registration shares.
/// </summary>
internal static class ChatClientTelemetryExtensions
{
    /// <summary>
    /// Adds OpenTelemetry to the pipeline under the source the exporter registration subscribes to.
    /// </summary>
    /// <remarks>
    /// An extension rather than the argument spelled out per provider: producer and exporter have to name
    /// the same source, and a provider that omits it loses its spans and token metrics silently. Must be
    /// applied before <c>UseToolTracking</c>, which must in turn precede <c>UseFunctionInvocation</c>.
    /// </remarks>
    /// <param name="builder">The pipeline being built.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    internal static ChatClientBuilder UseEnterpriseTelemetry(this ChatClientBuilder builder) =>
        builder.UseOpenTelemetry(sourceName: TelemetryRegistration.ChatTelemetrySourceName);
}
