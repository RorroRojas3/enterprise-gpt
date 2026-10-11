using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

/// <summary>Tuning for the Azure Monitor exporter.</summary>
internal sealed class TelemetryOptions
{
    internal const string SectionName = "AzureMonitor";

    /// <remarks>
    /// Carries an ingestion key, so it belongs in an environment variable or user secrets. The flat
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> key wins over this one.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>The cloud role name every telemetry item is reported under.</summary>
    public string RoleName { get; set; } = "enterprise-gpt-api";

    /// <summary>Ceiling on exported traces per second, or <see langword="null"/> to sample by ratio instead.</summary>
    /// <remarks>
    /// <c>appsettings.json</c> ships a value, so ratio sampling means clearing it with
    /// <c>AzureMonitor__TracesPerSecond=</c>. Clearing both samplers exports every trace.
    /// </remarks>
    public double? TracesPerSecond { get; set; } = 5.0;

    /// <summary>Fraction of traces to export, above zero and at most one.</summary>
    /// <remarks>Honoured only while <see cref="TracesPerSecond"/> is <see langword="null"/>; setting both is rejected at startup.</remarks>
    public float? SamplingRatio { get; set; }

    /// <summary>Whether log records inherit the sampling decision of the span they were written under.</summary>
    /// <remarks>
    /// Left off so the access log stays a census while spans are sampled: a request that was sampled out
    /// still has to be findable by the <c>traceId</c> its error response handed the caller.
    /// </remarks>
    public bool EnableTraceBasedLogsSampler { get; set; }

    public bool EnableLiveMetrics { get; set; } = true;

    /// <summary>Whether server spans carry the caller's Entra object id as <c>enduser.id</c>.</summary>
    public bool CaptureUserId { get; set; } = true;
}

internal sealed class TelemetryOptionsValidator : AbstractValidator<TelemetryOptions>
{
    public TelemetryOptionsValidator()
    {
        RuleFor(options => options.RoleName).NotEmpty().WithMessage("AzureMonitor:RoleName is required.");
        RuleFor(options => options.SamplingRatio).Null().When(options => options.TracesPerSecond is not null)
            .WithMessage("AzureMonitor:TracesPerSecond and AzureMonitor:SamplingRatio select different samplers — set at most one.");
        RuleFor(options => options.TracesPerSecond).Must(rate => rate is null or > 0)
            .WithMessage("AzureMonitor:TracesPerSecond must be greater than zero.");
        RuleFor(options => options.SamplingRatio).Must(ratio => ratio is null or (> 0 and <= 1))
            .WithMessage("AzureMonitor:SamplingRatio must be greater than zero and at most one.");
    }
}
