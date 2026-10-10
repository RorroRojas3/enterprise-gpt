using System.Runtime.CompilerServices;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// Locates the two places the spike's evidence lives: the copies in the test output, which every run
/// reads, and the originals in the source tree, which only a recording run writes.
/// </summary>
/// <remarks>
/// Reads go through the output directory so an ordinary <c>dotnet test</c> works from a published
/// bundle with no repository present. Writes use <see cref="CallerFilePathAttribute"/>, which is
/// meaningless off the machine that compiled the assembly — hence the existence checks.
/// </remarks>
internal static class SpikePaths
{
    /// <summary>Gets the evidence directory a run reads from — the copy beside the test assembly.</summary>
    public static string EvidenceOutputDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "FileAgentSpike", "Evidence");

    /// <summary>
    /// Gets the conversion matrix a run reads from — the copy <c>Enterprise.Gpt.Service.csproj</c>
    /// lays down, folder structure preserved rather than flattened.
    /// </summary>
    public static string ConversionMatrixOutputPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Agents", "Documents", ConversionMatrixFileName);

    public const string ConversionMatrixFileName = "conversion-matrix.json";

    /// <summary>Reads one probe program from the copy beside the test assembly.</summary>
    /// <remarks>Copied rather than embedded so a probe can be edited and re-run without a rebuild.</remarks>
    public static string ReadScript(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FileAgentSpike", "Scripts", fileName));

    /// <summary>Returns the evidence directory in the source tree, for a recording run to write into.</summary>
    /// <exception cref="InvalidOperationException">The source tree this assembly was compiled from is absent.</exception>
    public static string RequireEvidenceSourceDirectory() =>
        RequireSourceDirectory(Path.Combine(SpikeSourceDirectory(), "Evidence"));

    /// <summary>Returns the conversion matrix's source path, for a recording run to rewrite.</summary>
    /// <exception cref="InvalidOperationException">The source tree this assembly was compiled from is absent.</exception>
    public static string RequireConversionMatrixSourcePath()
    {
        // ...\tests\Enterprise.Gpt.Integration.Test\FileAgentSpike -> ...\Enterprise.Gpt.Service
        var solutionDirectory = Directory.GetParent(SpikeSourceDirectory())?.Parent?.Parent
            ?? throw SourceTreeMissing(SpikeSourceDirectory());

        var directory = RequireSourceDirectory(
            Path.Combine(solutionDirectory.FullName, "Enterprise.Gpt.Service", "Agents", "Documents"));

        return Path.Combine(directory, ConversionMatrixFileName);
    }

    private static string RequireSourceDirectory(string path) =>
        Directory.Exists(path) ? path : throw SourceTreeMissing(path);

    private static InvalidOperationException SourceTreeMissing(string path) =>
        new($"Recording mode writes evidence back into the source tree, and '{path}' does not exist on "
            + "this machine. Run the spike from a checkout of the repository this assembly was built "
            + $"from, or unset {SpikeConfiguration.SectionName}:Record to assert against the committed evidence instead.");

    private static string SpikeSourceDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)
        ?? throw new InvalidOperationException("The compiler supplied no path for this source file.");
}
