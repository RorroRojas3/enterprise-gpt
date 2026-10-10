using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

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
