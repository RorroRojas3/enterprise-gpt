using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// One question the spike asked of the live deployment, and what came back.
/// </summary>
/// <param name="Outcome">The short answer, so the file can be skimmed.</param>
/// <param name="Detail">The observation the outcome rests on.</param>
internal sealed record CapabilityFinding(string Question, string Outcome, string Detail);

/// <summary>
/// What the Responses route actually does in this tenant.
/// </summary>
/// <param name="Deployment">The deployment name, never the endpoint: the resource hostname is the one
/// tenant-identifying string that could land here, and it says nothing about the sandbox.</param>
/// <param name="Findings">Every question asked, in the order asked.</param>
internal sealed record CapabilityReport(
    DateTimeOffset RecordedUtc,
    string Deployment,
    IReadOnlyList<CapabilityFinding> Findings);

/// <summary>
/// One Python library the sandbox was asked about.
/// </summary>
/// <param name="Module">The import name, which is not always the package name.</param>
/// <param name="Package">The distribution name a skill would cite.</param>
internal sealed record InventoryLibrary(string Module, string Package, bool Importable, string? Version);

/// <summary>
/// The sandbox image as it stood on the day of the run.
/// </summary>
/// <param name="PythonVersion">The interpreter version, which dates the image as well as anything.</param>
/// <param name="OfficeConverter">The <c>soffice</c>/<c>libreoffice</c> binary, if any. Its presence
/// promotes every Office-to-PDF cell from structural to faithful.</param>
internal sealed record SandboxInventory(
    DateTimeOffset RecordedUtc,
    string PythonVersion,
    IReadOnlyList<InventoryLibrary> Libraries,
    OfficeConverterProbe OfficeConverter);

/// <summary>The result of looking for a headless office suite on the sandbox PATH.</summary>
internal sealed record OfficeConverterProbe(bool Present, string? Path, string? Version);

/// <summary>
/// Reads and writes the spike's evidence files.
/// </summary>
/// <remarks>
/// Indented for review and git diffing rather than for parsing. Nulls are written rather than omitted:
/// "not probed" and "probed and found nothing" must not read the same in a diff.
/// </remarks>
internal static class SpikeEvidence
{
    public const string CapabilityReportFileName = "capability-report.json";

    public const string SandboxInventoryFileName = "sandbox-inventory.json";

    /// <summary>The serializer settings every evidence file is written with.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Reads a committed evidence file from the copy beside the test assembly.</summary>
    /// <returns>The evidence, or <see langword="null"/> if it has not been recorded yet.</returns>
    public static T? Read<T>(string fileName)
        where T : class
    {
        var path = Path.Combine(SpikePaths.EvidenceOutputDirectory, fileName);

        return File.Exists(path)
            ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), SerializerOptions)
            : null;
    }

    /// <summary>Writes evidence back into the source tree, for review and commit.</summary>
    /// <returns>The path written, so the test can name it in its output.</returns>
    public static async Task<string> RecordAsync<T>(
        string fileName, T evidence, CancellationToken cancellationToken)
    {
        var path = Path.Combine(SpikePaths.RequireEvidenceSourceDirectory(), fileName);

        await File.WriteAllTextAsync(
            path, JsonSerializer.Serialize(evidence, SerializerOptions) + Environment.NewLine, cancellationToken);

        return path;
    }
}
