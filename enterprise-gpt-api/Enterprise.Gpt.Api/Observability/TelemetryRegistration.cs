using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Validation;
using FluentValidation;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Enterprise.Gpt.Api.Observability;

/// <summary>
/// Wires Application Insights up behind <see cref="ILogger{TCategoryName}"/> via the Azure Monitor
/// OpenTelemetry distro.
/// </summary>
/// <remarks>
/// The only place in the API that names a telemetry backend; moving to another provider means replacing this
/// file. The classic <c>Microsoft.ApplicationInsights.AspNetCore</c> SDK must not be used alongside the distro.
/// </remarks>
internal static class TelemetryRegistration
{
    /// <summary>Names both the chat clients' <c>ActivitySource</c> and their <c>Meter</c>.</summary>
    internal const string ChatTelemetrySourceName = TelemetryNames.ChatSource;

    // Reported as application_Version, so an exception can be attributed to the deployment that produced it.
    private static readonly string? BuildVersion = typeof(TelemetryRegistration).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    #region Public static methods

    /// <summary>Registers the Azure Monitor exporter when a connection string is configured.</summary>
    /// <remarks>
    /// The connection string carries an ingestion key, so never <c>appsettings.json</c>. With none, the
    /// distro is skipped, keeping local runs and the test host free of an exporter retrying in the background.
    /// </remarks>
    internal static IServiceCollection AddEnterpriseGptTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(TelemetryOptions.SectionName);

        // Bound even when the exporter below is skipped, so a malformed value fails the deployment that
        // wrote it rather than the first one that happens to configure a connection string.
        services.AddSingleton<IValidator<TelemetryOptions>, TelemetryOptionsValidator>();
        services.AddOptions<TelemetryOptions>()
            .Bind(section)
            .ValidateWithFluentValidation()
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

    /// <summary>Prefers the flat environment key App Service sets over the configured section.</summary>
    internal static string? ResolveConnectionString(IConfiguration configuration, TelemetryOptions telemetry) =>
        configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] ?? telemetry.ConnectionString;

    #endregion
}
