namespace Enterprise.Gpt.Api.Observability;

/// <summary>
/// Tuning for the Azure Monitor exporter, bound from the <c>AzureMonitor</c> configuration section.
/// </summary>
internal sealed class TelemetryOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    internal const string SectionName = "AzureMonitor";

    /// <summary>Connection string for the target Application Insights resource.</summary>
    /// <remarks>
    /// It carries an ingestion key, so it belongs in an environment variable or user secrets, never in
    /// <c>appsettings.json</c>. The flat <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> key wins over this one.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>The cloud role name every telemetry item is reported under.</summary>
    public string RoleName { get; set; } = "enterprise-gpt-api";

    /// <summary>Ceiling on exported traces per second, or <see langword="null"/> to sample by ratio instead.</summary>
    /// <remarks>
    /// The distro's own default, restated here so the sampling posture is a deployment decision rather than
    /// a library default nobody chose. Clearing this and <see cref="SamplingRatio"/> both exports every trace.
    /// Since <c>appsettings.json</c> ships a value, selecting ratio sampling means clearing this one with an
    /// empty setting — <c>AzureMonitor__TracesPerSecond=</c>.
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

    /// <summary>Whether the Live Metrics stream is enabled.</summary>
    public bool EnableLiveMetrics { get; set; } = true;

    /// <summary>Whether server spans carry the caller's Entra object id as <c>enduser.id</c>.</summary>
    public bool CaptureUserId { get; set; } = true;
}
