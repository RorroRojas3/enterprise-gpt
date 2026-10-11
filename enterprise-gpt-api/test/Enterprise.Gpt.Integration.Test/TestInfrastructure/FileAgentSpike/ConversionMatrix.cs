using System.Text.Json;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// The conversion matrix: which format pairs this platform serves, and at what fidelity.
/// </summary>
/// <param name="SchemaVersion">The document's shape, so a reader can refuse one it does not know.</param>
/// <param name="Status"><c>proposed</c> until a run has attempted every offered pair, then <c>confirmed</c>.</param>
/// <param name="RecordedUtc">When the confirming run finished, or <see langword="null"/> for a matrix still proposed.</param>
/// <param name="Deployment">The deployment the confirming run used, by name and never by endpoint; <see langword="null"/> for a matrix still proposed.</param>
/// <param name="OfficeConverterPresent">Whether the image carried a headless office suite, which is
/// what separates a faithful Office-to-PDF render from a structural one.</param>
/// <param name="Formats">The formats in play, in the order the published table lists them.</param>
/// <param name="TierGlyphs">The glyph each tier renders as, so the table and this file cannot disagree.</param>
/// <param name="Cells">Every off-diagonal pair. Forty-two of them, and none may be missing.</param>
/// <remarks>
/// The single source for both the <c>document-conversion</c> skill and <c>FileAgentOptions</c>, so a
/// pair cannot be refused in one place and offered in another. It lives in
/// <c>Enterprise.Gpt.Service</c> as a published deliverable, not a test fixture.
/// </remarks>
internal sealed record ConversionMatrix(
    int SchemaVersion,
    string Status,
    DateTimeOffset? RecordedUtc,
    string? Deployment,
    bool? OfficeConverterPresent,
    IReadOnlyList<string> Formats,
    IReadOnlyDictionary<string, string> TierGlyphs,
    IReadOnlyList<ConversionCell> Cells)
{
    /// <summary>The tier a conversion carries when nothing the source expressed is lost.</summary>
    public const string Faithful = "faithful";

    /// <summary>The tier a conversion carries when content survives but presentation does not.</summary>
    public const string Structural = "structural";

    /// <summary>The tier a pair carries when no path in this sandbox is worth serving.</summary>
    public const string Refused = "refused";

    /// <summary>The tier a pair carries when v1 simply does not offer it.</summary>
    public const string NotOffered = "notOffered";

    /// <summary>The status a matrix carries once every cell is confirmed.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Reads the matrix from the copy the Service project laid down beside the test assembly.</summary>
    public static ConversionMatrix Load() =>
        JsonSerializer.Deserialize<ConversionMatrix>(
            File.ReadAllText(SpikePaths.ConversionMatrixOutputPath), SpikeEvidence.SerializerOptions)
        ?? throw new InvalidOperationException(
            $"'{SpikePaths.ConversionMatrixOutputPath}' deserialized to nothing, so the conversion matrix is unreadable.");

    /// <summary>Writes the matrix back over its source original, for review and commit.</summary>
    /// <returns>The path written.</returns>
    public async Task<string> RecordAsync(CancellationToken cancellationToken)
    {
        var path = SpikePaths.RequireConversionMatrixSourcePath();

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(this, SpikeEvidence.SerializerOptions) + Environment.NewLine,
            cancellationToken);

        return path;
    }

    /// <summary>Gets the cells whose pair is offered at all, which is what a probe run attempts.</summary>
    /// <remarks>A <see cref="NotOffered"/> pair is not a refusal, so there is nothing to disprove.</remarks>
    public IEnumerable<ConversionCell> OfferedCells =>
        Cells.Where(cell => cell.ProposedTier != NotOffered);
}
