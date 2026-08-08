using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Resources;

namespace Enterprise.Gpt.Api.Observability;

/// <summary>
/// Wires Application Insights up behind <see cref="ILogger{TCategoryName}"/> via the Azure Monitor
/// OpenTelemetry distro.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in the API that names a telemetry backend. <c>UseAzureMonitor</c> registers an
/// OpenTelemetry <see cref="ILoggerProvider"/> alongside the trace and metric exporters, so every record
/// the application already writes through <c>ILogger</c> reaches Application Insights as a <c>trace</c>,
/// correlated by <c>operation_Id</c> with the <c>request</c> telemetry the ASP.NET Core instrumentation
/// collects — which is the same 32-hex W3C trace id the API returns as <c>traceId</c> on problem
/// responses. Moving to Serilog, or to any other provider, means replacing this file and nothing else.
/// </para>
/// <para>
/// The distro is the current recommendation for new ASP.NET Core applications; the classic
/// <c>Microsoft.ApplicationInsights.AspNetCore</c> SDK is in maintenance and, per Microsoft's guidance,
/// must not be used alongside it.
/// </para>
/// </remarks>
internal static class TelemetryRegistration
{
    /// <summary>
    /// The name the Microsoft.Extensions.AI chat clients emit telemetry under. It names both their
    /// <c>ActivitySource</c> and their <c>Meter</c>, which is why the registration below adds it as a
    /// source <em>and</em> a meter.
    /// </summary>
    /// <remarks>
    /// Named explicitly at both the producer (<c>UseOpenTelemetry(sourceName:)</c>) and the consumer so
    /// the two cannot drift apart when the library changes its own default.
    /// </remarks>
    internal const string ChatTelemetrySourceName = "Enterprise.Gpt.Chat";

    /// <summary>The service name reported as the Application Insights cloud role.</summary>
    private const string ServiceName = "enterprise-gpt-api";

    /// <summary>
    /// Registers the Azure Monitor exporter when a connection string is configured.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">
    /// Read for <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> first, then <c>AzureMonitor:ConnectionString</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <remarks>
    /// The connection string carries an ingestion key, so it is treated as a secret: an environment
    /// variable in deployed environments (App Service sets it automatically), user secrets locally, and
    /// never <c>appsettings.json</c>. With none configured the whole distro is skipped rather than
    /// started with nowhere to send to — which is what keeps local runs and the integration test host
    /// free of an exporter retrying in the background.
    /// </remarks>
    internal static IServiceCollection AddEnterpriseTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
            ?? configuration["AzureMonitor:ConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .UseAzureMonitor(options => options.ConnectionString = connectionString)
            .WithTracing(tracing => tracing.AddSource(ChatTelemetrySourceName))
            // The meter as well as the source: OpenTelemetryChatClient builds both from the same name, and
            // gen_ai.client.token.usage is the metric that turns LLM spend into something observable.
            .WithMetrics(metrics => metrics.AddMeter(ChatTelemetrySourceName));

        return services;
    }
}
