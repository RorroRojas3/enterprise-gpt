using Xunit;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// Records which Python libraries the sandbox image carries, and whether a headless office suite is on
/// its PATH.
/// </summary>
/// <remarks>
/// A recording run writes <c>Evidence/sandbox-inventory.json</c>; every run after diffs the live image
/// against it and fails naming what moved — the fixture is a tripwire, not a transcript. Versions are
/// compared at the major component only: a pandas patch bump is not a finding, fpdf turning out to be
/// 1.x when a skill targets the 2.x API is.
/// </remarks>
[Collection(SandboxSpikeCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SandboxInventoryTests(SandboxSpikeFixture fixture)
{
    private readonly SandboxSpikeFixture _fixture = fixture;

    [Fact]
    public async Task SandboxImage_Inventoried_MatchesTheRecordedFixture()
    {
        Assert.SkipUnless(_fixture.IsEnabled, SpikeConfiguration.SkipReason);

        var live = await _fixture.InventoryAsync(TestContext.Current.CancellationToken);

        var present = live.Libraries.Where(library => library.Importable).ToList();
        var absent = live.Libraries.Where(library => !library.Importable).ToList();

        _fixture.Record(
            "Which of the libraries the skills would name does the sandbox actually carry?",
            $"{present.Count} of {live.Libraries.Count} import",
            $"Python {live.PythonVersion}. Present: {Describe(present)}. Absent: "
            + $"{(absent.Count > 0 ? string.Join(", ", absent.Select(library => library.Package)) : "none")}.");

        _fixture.Record(
            "Is a headless office suite available for Office to PDF conversion?",
            live.OfficeConverter.Present ? "yes" : "no",
            live.OfficeConverter.Present
                ? $"Found at {live.OfficeConverter.Path}, reporting {live.OfficeConverter.Version}. Every "
                  + "Office to PDF cell can be re-attempted through it and promoted to faithful."
                : "No soffice, libreoffice or lowriter binary is on PATH, so every Office to PDF cell "
                  + "stays at the structural tier and the answer must say the typography is the sandbox's.");

        Assert.NotEmpty(live.PythonVersion);

        if (SpikeConfiguration.IsRecording)
        {
            var path = await SpikeEvidence.RecordAsync(
                SpikeEvidence.SandboxInventoryFileName, live, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));

            return;
        }

        var recorded = SpikeEvidence.Read<SandboxInventory>(SpikeEvidence.SandboxInventoryFileName);

        Assert.True(
            recorded is not null,
            "No sandbox inventory has been recorded yet, so there is nothing to diff against. Run once "
            + $"with {SpikeConfiguration.SectionName}:Record set to true, review the emitted "
            + $"{SpikeEvidence.SandboxInventoryFileName}, and commit it.");

        var drift = Drift(recorded!, live);

        Assert.True(
            drift.Count == 0,
            "The sandbox image has changed since the inventory was recorded, which invalidates every "
            + "skill written against it and every cell of the conversion matrix that names an affected "
            + $"library:{Environment.NewLine}{string.Join(Environment.NewLine, drift)}");
    }

    private static string Describe(IEnumerable<InventoryLibrary> libraries) =>
        string.Join(", ", libraries.Select(library => $"{library.Package} {library.Version ?? "(unversioned)"}"));

    private static List<string> Drift(SandboxInventory recorded, SandboxInventory live)
    {
        var previous = recorded.Libraries.ToDictionary(library => library.Module, StringComparer.Ordinal);
        List<string> drift = [];

        foreach (var library in live.Libraries)
        {
            if (!previous.TryGetValue(library.Module, out var before))
            {
                drift.Add($"  {library.Package}: not in the recorded inventory at all.");
                continue;
            }

            if (before.Importable != library.Importable)
            {
                drift.Add($"  {library.Package}: was {(before.Importable ? "present" : "absent")}, is now "
                    + $"{(library.Importable ? "present" : "absent")}.");
                continue;
            }

            if (MajorOf(before.Version) != MajorOf(library.Version))
            {
                drift.Add($"  {library.Package}: was {before.Version ?? "(unversioned)"}, is now "
                    + $"{library.Version ?? "(unversioned)"}.");
            }
        }

        foreach (var before in recorded.Libraries.Where(library => live.Libraries.All(current => current.Module != library.Module)))
        {
            drift.Add($"  {before.Package}: was inventoried and is no longer probed.");
        }

        if (recorded.OfficeConverter.Present != live.OfficeConverter.Present)
        {
            drift.Add($"  soffice/libreoffice: was {(recorded.OfficeConverter.Present ? "present" : "absent")}, "
                + $"is now {(live.OfficeConverter.Present ? "present" : "absent")}. Every Office to PDF cell's "
                + "tier depends on this.");
        }

        return drift;
    }

    private static string MajorOf(string? version) =>
        version is { Length: > 0 } ? version.Split('.')[0] : string.Empty;
}
