using Xunit;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// The one sandbox conversation every probe shares, and the findings they accumulate into it.
/// </summary>
/// <remarks>
/// Shared because a container is billed per minute with a five-minute minimum and is reused only
/// while the conversation that created it stays in context. A collection fixture also serializes the
/// classes using it, which the probes rely on: they mutate one shared
/// <c>HostedCodeInterpreterTool.Inputs</c> list between runs.
/// </remarks>
public sealed class SandboxSpikeFixture : IAsyncLifetime
{
    // Whole exchanges. Three rather than one because a container falling out of context costs a
    // fresh five-minute minimum, well above what three kept turns cost in input tokens.
    private const int MaxTurns = 3;

    private readonly List<CapabilityFinding> _findings = [];
    private readonly Lock _gate = new();

    private SandboxSession? _session;
    private Task<SandboxInventory>? _inventory;

    /// <summary>Gets whether the spike is opted in and configured on this machine.</summary>
    public bool IsEnabled => SpikeConfiguration.IsEnabled;

    /// <summary>Gets the shared sandbox conversation.</summary>
    /// <exception cref="InvalidOperationException">The spike is not enabled, so no session was opened —
    /// a test reaching this skipped its own <see cref="Assert.SkipUnless(bool, string)"/> guard.</exception>
    internal SandboxSession Session =>
        _session ?? throw new InvalidOperationException(
            $"No sandbox session was opened because the spike is not enabled. {SpikeConfiguration.SkipReason}");

    /// <summary>Inventories the sandbox image, once per run however the suite is filtered.</summary>
    /// <remarks>
    /// The task is cached rather than the value, so a failed probe is not silently paid for again —
    /// it is a billable run either way. Unsynchronised because the collection fixture serializes
    /// every class sharing this session.
    /// </remarks>
    internal Task<SandboxInventory> InventoryAsync(CancellationToken cancellationToken) =>
        _inventory ??= SandboxInventoryProbe.RunAsync(Session, cancellationToken);

    /// <summary>Records one finding for <see cref="SpikeEvidence.CapabilityReportFileName"/>.</summary>
    public void Record(string question, string outcome, string detail)
    {
        lock (_gate)
        {
            _findings.Add(new CapabilityFinding(question, outcome, detail));
        }
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync()
    {
        if (IsEnabled)
        {
            _session = SandboxSession.Create(MaxTurns);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_session is null)
        {
            return;
        }

        // A filtered run records a partial report on purpose — visibly covering less beats quietly
        // discarding findings. Disposal is in a finally because RequireEvidenceSourceDirectory throws
        // by design off the build machine, and skipping it there would leak every uploaded file.
        try
        {
            // Before the report is written: a leak it never mentions is a leak nobody finds.
            var leaked = await _session.ReleaseUploadsAsync();

            if (leaked.Count > 0)
            {
                Record(
                    "Were the files this run uploaded cleaned up afterwards?",
                    $"no, {leaked.Count} survived",
                    $"These remain against the account's file quota and need deleting by hand: {string.Join(", ", leaked)}.");
            }

            if (SpikeConfiguration.IsRecording && _findings.Count > 0)
            {
                var report = new CapabilityReport(
                    DateTimeOffset.UtcNow,
                    _session.DeploymentName,
                    [.. _findings]);

                await SpikeEvidence.RecordAsync(SpikeEvidence.CapabilityReportFileName, report, CancellationToken.None);
            }
        }
        finally
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }
}

/// <summary>
/// Serializes every sandbox probe onto one session.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SandboxSpikeCollection : ICollectionFixture<SandboxSpikeFixture>
{
    public const string Name = "FileAgentSpike";
}
