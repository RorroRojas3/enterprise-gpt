using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Api.Observability;

internal static class ChatClientTelemetryExtensions
{
    #region Public static methods

    /// <remarks>
    /// One extension so producer and exporter always name the same source: a provider that omits it loses
    /// its spans and token metrics silently. Order: this, <c>UseToolTracking</c>, <c>UseFunctionInvocation</c>.
    /// </remarks>
    internal static ChatClientBuilder UseEnterpriseGptTelemetry(this ChatClientBuilder builder) =>
        builder.UseOpenTelemetry(sourceName: TelemetryRegistration.ChatTelemetrySourceName);

    #endregion
}
