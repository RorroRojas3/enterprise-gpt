using System.Text.Json;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// What one attempt at one conversion pair actually did.
/// </summary>
/// <param name="Engine">The libraries that performed it, or <see langword="null"/> if none got that far.</param>
/// <param name="Verified">Whether the harness could open the result and find the shape it asked for.</param>
/// <param name="Detail">The verification's own account, or the failure that stopped the attempt.</param>
internal sealed record ConversionEvidence(
    DateTimeOffset RecordedUtc,
    string? Engine,
    bool Produced,
    long Bytes,
    bool Verified,
    string Detail);

/// <summary>
/// One cell of the conversion matrix.
/// </summary>
/// <param name="ProposedTier">The tier expected before anything ran. Kept alongside
/// <paramref name="Tier"/> so a demotion reads as a demotion.</param>
/// <param name="Tier">The confirmed tier, or the proposal until a run confirms it.</param>
/// <param name="Note">What the answer has to tell the user about this pair.</param>
internal sealed record ConversionCell(
    string From,
    string To,
    string ProposedTier,
    string Tier,
    bool Confirmed,
    string? Engine,
    string Note,
    ConversionEvidence? Evidence);

/// <summary>
/// The conversion matrix: which format pairs this platform serves, and at what fidelity.
/// </summary>
/// <param name="SchemaVersion">The document's shape, so a reader can refuse one it does not know.</param>
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
