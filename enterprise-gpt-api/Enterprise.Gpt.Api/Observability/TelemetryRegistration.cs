using Azure.Monitor.OpenTelemetry.AspNetCore;
using Enterprise.Gpt.Common.Observability;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Reflection;

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
    /// Producer and consumer must name the same source, so the literal lives in <c>Common</c>, which
    /// both ends can reach.
    /// </remarks>
    internal const string ChatTelemetrySourceName = TelemetryNames.ChatSource;

    /// <summary>
    /// The build identity reported as <c>application_Version</c>, so an exception can be attributed to
    /// the deployment that produced it.
    /// </summary>
    private static readonly string? BuildVersion = typeof(TelemetryRegistration).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>
    /// Registers the Azure Monitor exporter when a connection string is configured.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">
    /// Read for <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> first, then <c>AzureMonitor:ConnectionString</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <remarks>
    /// The connection string carries an ingestion key, so it belongs in an environment variable or user
    /// secrets, never in <c>appsettings.json</c>. With none configured the whole distro is skipped, which
    /// is what keeps local runs and the test host free of an exporter retrying in the background.
    /// </remarks>
    internal static IServiceCollection AddEnterpriseTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(TelemetryOptions.SectionName);

        // Bound even when the exporter below is skipped, so a malformed value fails the deployment that
        // wrote it rather than the first one that happens to configure a connection string.
        services.AddOptions<TelemetryOptions>()
            .Bind(section)
            .Validate(options => !string.IsNullOrWhiteSpace(options.RoleName),
                "AzureMonitor:RoleName is required.")
            .Validate(options => options.TracesPerSecond is null || options.SamplingRatio is null,
                "AzureMonitor:TracesPerSecond and AzureMonitor:SamplingRatio select different samplers — set at most one.")
            .Validate(options => options.TracesPerSecond is null or > 0,
                "AzureMonitor:TracesPerSecond must be greater than zero.")
            .Validate(options => options.SamplingRatio is null or (> 0 and <= 1),
                "AzureMonitor:SamplingRatio must be greater than zero and at most one.")
            .ValidateOnStart();

        var telemetry = section.Get<TelemetryOptions>() ?? new TelemetryOptions();

        var connectionString = ResolveConnectionString(configuration, telemetry);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.AddSingleton<EndUserEnrichingProcessor>();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(telemetry.RoleName, serviceVersion: BuildVersion))
            // Ahead of UseAzureMonitor: that call appends the exporter, and a processor added after it
            // would run against spans the exporter had already been handed.
            .WithTracing(tracing => tracing
                .AddSource(ChatTelemetrySourceName)
                .AddProcessor<EndUserEnrichingProcessor>())
            // The meter as well as the source: OpenTelemetryChatClient builds both from the same name, and
            // gen_ai.client.token.usage is the metric that turns LLM spend into something observable.
            .WithMetrics(metrics => metrics.AddMeter(ChatTelemetrySourceName))
            .UseAzureMonitor(options =>
            {
                options.ConnectionString = connectionString;
                options.EnableLiveMetrics = telemetry.EnableLiveMetrics;
                options.EnableTraceBasedLogsSampler = telemetry.EnableTraceBasedLogsSampler;

                // Assigning null is meaningful: it is what switches the distro off rate-limited sampling
                // and onto the fixed ratio below.
                options.TracesPerSecond = telemetry.TracesPerSecond;

                if (telemetry.SamplingRatio is { } ratio)
                {
                    options.SamplingRatio = ratio;
                }
            });

        return services;
    }

    /// <summary>
    /// Resolves the connection string, preferring the flat environment key App Service sets over the
    /// configured section.
    /// </summary>
    /// <param name="configuration">Configuration to read the flat key from.</param>
    /// <param name="telemetry">Options already bound from the <c>AzureMonitor</c> section.</param>
    /// <returns>The connection string, or <see langword="null"/> when neither source supplies one.</returns>
    internal static string? ResolveConnectionString(IConfiguration configuration, TelemetryOptions telemetry) =>
        configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] ?? telemetry.ConnectionString;
}
