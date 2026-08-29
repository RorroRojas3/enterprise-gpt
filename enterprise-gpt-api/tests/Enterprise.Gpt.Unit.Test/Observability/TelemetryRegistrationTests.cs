using Azure.Monitor.OpenTelemetry.AspNetCore;
using Enterprise.Gpt.Api.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Observability;

/// <summary>
/// Covers the exporter registration and the options that decide the deployment's sampling posture.
/// </summary>
/// <remarks>
/// Assertions read the service descriptors rather than resolving them: building a
/// <c>TracerProvider</c> would construct the Azure Monitor exporter against the fake connection
/// string below and reach the network.
/// </remarks>
public sealed class TelemetryRegistrationTests
{
    private const string EnvironmentKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    private const string ConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.in.applicationinsights.azure.com/";

    private const string OtherConnectionString =
        "InstrumentationKey=11111111-1111-1111-1111-111111111111;IngestionEndpoint=https://other.in.applicationinsights.azure.com/";

    [Fact]
    public void AddEnterpriseTelemetry_NoConnectionString_SkipsTheDistroEntirely()
    {
        var services = Register([]);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TracerProvider));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(MeterProvider));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(EndUserEnrichingProcessor));
    }

    [Fact]
    public void AddEnterpriseTelemetry_ConnectionStringInSection_RegistersTracesMetricsAndTheEnricher()
    {
        var services = Register(new() { ["AzureMonitor:ConnectionString"] = ConnectionString });

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TracerProvider));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(MeterProvider));
        // AddProcessor<T>() would TryAddSingleton this itself; the explicit registration is what makes
        // the enricher's own dependencies visible at the registration site, and is asserted here so a
        // cleanup that drops it has to decide that deliberately.
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(EndUserEnrichingProcessor));
    }

    /// <summary>
    /// The values the exporter actually runs on. Nothing else asserts them, and a dropped assignment
    /// here is invisible: the distro simply keeps its own default and the deployment samples at a rate
    /// nobody chose.
    /// </summary>
    [Fact]
    public void AddEnterpriseTelemetry_ConfiguredOptions_ReachTheExporter()
    {
        var services = Register(new()
        {
            [EnvironmentKey] = ConnectionString,
            ["AzureMonitor:RoleName"] = "enterprise-gpt-api-slot",
            ["AzureMonitor:TracesPerSecond"] = "20",
            ["AzureMonitor:EnableLiveMetrics"] = "false",
            ["AzureMonitor:EnableTraceBasedLogsSampler"] = "true"
        });

        var options = Resolve(services);

        Assert.Equal(ConnectionString, options.ConnectionString);
        Assert.Equal(20d, options.TracesPerSecond);
        Assert.False(options.EnableLiveMetrics);
        Assert.True(options.EnableTraceBasedLogsSampler);
    }

    /// <summary>
    /// Clearing <c>TracesPerSecond</c> is what selects the fixed-ratio sampler; leaving the assignment
    /// out would keep rate-limited sampling and silently ignore the configured ratio.
    /// </summary>
    [Fact]
    public void AddEnterpriseTelemetry_ARatioWithNoRateCeiling_SelectsFixedRatioSampling()
    {
        var services = Register(new()
        {
            [EnvironmentKey] = ConnectionString,
            ["AzureMonitor:TracesPerSecond"] = null,
            ["AzureMonitor:SamplingRatio"] = "0.25"
        });

        var options = Resolve(services);

        Assert.Null(options.TracesPerSecond);
        Assert.Equal(0.25f, options.SamplingRatio);
    }

    [Fact]
    public void AddEnterpriseTelemetry_NoRatioConfigured_LeavesTheExporterRatioAlone()
    {
        var options = Resolve(Register(new() { [EnvironmentKey] = ConnectionString }));

        Assert.Equal(1f, options.SamplingRatio);
        Assert.Equal(5d, options.TracesPerSecond);
    }

    [Fact]
    public void AddEnterpriseTelemetry_ConnectionStringInEnvironmentKey_RegistersTheDistro()
    {
        var services = Register(new() { [EnvironmentKey] = ConnectionString });

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TracerProvider));
    }

    [Fact]
    public void ResolveConnectionString_BothSourcesConfigured_PrefersTheEnvironmentKey()
    {
        var configuration = Configuration(new() { [EnvironmentKey] = ConnectionString });

        var resolved = TelemetryRegistration.ResolveConnectionString(
            configuration,
            new TelemetryOptions { ConnectionString = OtherConnectionString });

        Assert.Equal(ConnectionString, resolved);
    }

    [Fact]
    public void AddEnterpriseTelemetry_DefaultConfiguration_PassesStartupValidation()
    {
        Validator(Register([])).Validate();
    }

    /// <remarks>
    /// The API's own <c>appsettings.json</c>, linked into this project's output rather than copied into
    /// a literal here, so a value edited in the real file cannot leave this test green.
    /// </remarks>
    [Fact]
    public void AddEnterpriseTelemetry_TheShippedDefaults_PassStartupValidationAndShipNoConnectionString()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile("api-appsettings.json").Build();

        var services = new ServiceCollection();
        services.AddEnterpriseTelemetry(configuration);

        Validator(services).Validate();
        // The connection string carries an ingestion key; appsettings.json is committed.
        Assert.Null(TelemetryRegistration.ResolveConnectionString(
            configuration,
            configuration.GetSection("AzureMonitor").Get<TelemetryOptions>()!));
    }

    /// <summary>
    /// The two settings select different samplers, so honouring both is impossible and silently
    /// honouring one hides which sampler is actually running.
    /// </summary>
    [Fact]
    public void AddEnterpriseTelemetry_BothSamplersConfigured_FailsStartupValidation()
    {
        var services = Register(new()
        {
            ["AzureMonitor:TracesPerSecond"] = "5",
            ["AzureMonitor:SamplingRatio"] = "0.5"
        });

        Assert.Throws<OptionsValidationException>(Validator(services).Validate);
    }

    [Theory]
    [InlineData("AzureMonitor:SamplingRatio", "0")]
    [InlineData("AzureMonitor:SamplingRatio", "1.5")]
    [InlineData("AzureMonitor:TracesPerSecond", "0")]
    [InlineData("AzureMonitor:RoleName", "")]
    public void AddEnterpriseTelemetry_OutOfRangeOption_FailsStartupValidation(string key, string value)
    {
        var settings = new Dictionary<string, string?> { [key] = value };

        if (key == "AzureMonitor:SamplingRatio")
        {
            settings["AzureMonitor:TracesPerSecond"] = null;
        }

        Assert.Throws<OptionsValidationException>(Validator(Register(settings)).Validate);
    }

    /// <summary>
    /// A local run configures no connection string, and a value it cannot honour is still a value
    /// somebody meant — failing here is what stops it reaching production unnoticed.
    /// </summary>
    [Fact]
    public void AddEnterpriseTelemetry_NoConnectionStringButBadOptions_StillFailsStartupValidation()
    {
        var services = Register(new() { ["AzureMonitor:SamplingRatio"] = "9" });

        Assert.Throws<OptionsValidationException>(Validator(services).Validate);
    }

    private static IServiceCollection Register(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddEnterpriseTelemetry(Configuration(settings));

        return services;
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static IStartupValidator Validator(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IStartupValidator>();

    /// <remarks>
    /// The distro registers its own configuration through the options system, so the delegate
    /// <c>UseAzureMonitor</c> was handed materializes here without building a provider or reaching the
    /// network.
    /// </remarks>
    private static AzureMonitorOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<AzureMonitorOptions>>().Value;
}
