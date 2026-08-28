using System.Text.Json;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// Runs the library-and-binary inventory in the sandbox and parses what it printed.
/// </summary>
/// <remarks>
/// Separate from the test that asserts on it because two suites need the same answer and neither may
/// depend on the other having run: one diffs the whole inventory against its fixture, the other reads
/// only whether a headless office suite exists. The fixture caches it so the probe is paid for once.
/// </remarks>
internal static class SandboxInventoryProbe
{
    /// <summary>Inventories the sandbox image.</summary>
    public static async Task<SandboxInventory> RunAsync(SandboxSession session, CancellationToken cancellationToken)
    {
        session.SetInputs();

        var probe = await SpikeResults.RunProbeAsync(
            session, SpikePaths.ReadScript("inventory.py"), "inventory.json", cancellationToken);

        using var result = probe.Result;

        return Parse(result.RootElement);
    }

    private static SandboxInventory Parse(JsonElement root) =>
        new(
            DateTimeOffset.UtcNow,
            root.GetProperty("pythonVersion").GetString() ?? string.Empty,
            [.. root.GetProperty("libraries").EnumerateArray().Select(library => new InventoryLibrary(
                library.GetProperty("module").GetString() ?? string.Empty,
                library.GetProperty("package").GetString() ?? string.Empty,
                library.GetProperty("importable").GetBoolean(),
                library.GetProperty("version").GetString()))],
            new OfficeConverterProbe(
                root.GetProperty("officeConverter").GetProperty("present").GetBoolean(),
                root.GetProperty("officeConverter").GetProperty("path").GetString(),
                root.GetProperty("officeConverter").GetProperty("version").GetString()));
}
