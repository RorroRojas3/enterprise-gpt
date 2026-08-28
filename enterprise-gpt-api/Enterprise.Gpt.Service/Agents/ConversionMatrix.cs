using System.Text.Json;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// How much of a source survives a conversion, as the confirmed matrix records it.
/// </summary>
public enum ConversionTiers
{
    /// <summary>Not proposed for this version. Not a refusal — no natural request maps to the pair.</summary>
    NotOffered = 0,

    /// <summary>Nothing the source expressed is lost. No caveat needed.</summary>
    Faithful = 1,

    /// <summary>
    /// Content, headings, tables and lists survive; pagination, typography and vendor layout do not.
    /// A conversion to serve with its caveat, never a reason to decline.
    /// </summary>
    Structural = 2,

    /// <summary>No path in this sandbox produces a result worth handing over.</summary>
    Refused = 3
}

/// <summary>One cell of the confirmed conversion matrix.</summary>
/// <param name="Note">What the answer has to say about this pair, in the matrix's own words.</param>
public sealed record ConversionCell(string From, string To, ConversionTiers Tier, string Note);

/// <summary>
/// The confirmed source-to-target conversion matrix.
/// </summary>
/// <remarks>
/// One source for two readers that must never disagree: the <c>document-conversion</c> skill renders
/// its table from this file, and the run's own pre-flight refuses a pair from the same cells.
/// </remarks>
public interface IConversionMatrix
{
    /// <summary>Gets the formats the matrix covers, in its own published order.</summary>
    IReadOnlyList<string> Formats { get; }

    /// <summary>Gets every cell, one per ordered pair of distinct formats.</summary>
    IReadOnlyList<ConversionCell> Cells { get; }

    /// <summary>
    /// Finds the cell for a pair.
    /// </summary>
    /// <param name="from">The source format, with or without a leading dot.</param>
    /// <param name="to">The target format, on the same terms.</param>
    /// <returns>The cell, or <see langword="null"/> when either format is not one the matrix covers.</returns>
    ConversionCell? Find(string? from, string? to);

    /// <summary>
    /// Lists the targets a source can actually reach, for the sentence a refusal ends on.
    /// </summary>
    /// <param name="from">The source format, with or without a leading dot.</param>
    /// <returns>The faithful and structural targets, in the matrix's published order.</returns>
    IReadOnlyList<string> OfferedTargets(string? from);
}

/// <inheritdoc />
public sealed class ConversionMatrix : IConversionMatrix
{
    /// <summary>The path the matrix is deployed to, relative to the build output.</summary>
    public static readonly string DeployedPath =
        Path.Combine(AppContext.BaseDirectory, "Agents", "Documents", "conversion-matrix.json");

    private readonly Dictionary<(string From, string To), ConversionCell> _byPair;

    private ConversionMatrix(IReadOnlyList<string> formats, IReadOnlyList<ConversionCell> cells)
    {
        Formats = formats;
        Cells = cells;
        _byPair = cells.ToDictionary(cell => (cell.From, cell.To));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Formats { get; }

    /// <inheritdoc />
    public IReadOnlyList<ConversionCell> Cells { get; }

    /// <summary>
    /// Reads the published matrix off disk.
    /// </summary>
    /// <param name="path">The file to read, defaulting to <see cref="DeployedPath"/>.</param>
    /// <returns>The parsed matrix.</returns>
    /// <exception cref="FileNotFoundException">The matrix did not ship with this build.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is not a matrix this build can read — malformed JSON, a missing property, a duplicated
    /// pair, or no cells at all. One exception for every shape of wrong, because this runs inside the DI
    /// factory and a startup failure has to name the file it could not read.
    /// </exception>
    public static ConversionMatrix Load(string? path = null)
    {
        var file = path ?? DeployedPath;

        if (!File.Exists(file))
        {
            throw new FileNotFoundException(
                $"The confirmed conversion matrix was expected at '{file}' and did not ship with this build.", file);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;

            List<string> formats =
            [
                .. root.GetProperty("formats").EnumerateArray().Select(format => Normalize(format.GetString()))
            ];

            List<ConversionCell> cells =
            [
                .. root.GetProperty("cells").EnumerateArray().Select(cell => new ConversionCell(
                    Normalize(cell.GetProperty("from").GetString()),
                    Normalize(cell.GetProperty("to").GetString()),
                    ParseTier(cell.GetProperty("tier").GetString()),
                    cell.GetProperty("note").GetString() ?? string.Empty))
            ];

            return cells.Count > 0
                ? new ConversionMatrix(formats, cells)
                : throw new InvalidDataException($"The conversion matrix at '{file}' carries no cells.");
        }
        // InvalidOperationException among them: a document that parses but is the wrong shape — a
        // number where a string belongs, an object where an array does — raises that rather than a
        // JsonException, and it would otherwise reach startup without naming the file.
        catch (Exception exception)
            when (exception is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"The conversion matrix at '{file}' could not be read.", exception);
        }
    }

    /// <inheritdoc />
    public ConversionCell? Find(string? from, string? to) =>
        _byPair.GetValueOrDefault((Normalize(from), Normalize(to)));

    /// <inheritdoc />
    public IReadOnlyList<string> OfferedTargets(string? from)
    {
        var source = Normalize(from);

        return
        [
            .. Formats.Where(target =>
                _byPair.TryGetValue((source, target), out var cell)
                && cell.Tier is ConversionTiers.Faithful or ConversionTiers.Structural)
        ];
    }

    /// <summary>Lower-cases a format and drops a leading dot, so ".DOCX" and "docx" are one key.</summary>
    private static string Normalize(string? format) =>
        format is null ? string.Empty : format.Trim().TrimStart('.').ToLowerInvariant();

    // An unknown tier reads as notOffered rather than throwing: a matrix regenerated with a tier this
    // build does not know about must not take the whole agent down, and "not offered" is the arm that
    // neither promises nor refuses.
    private static ConversionTiers ParseTier(string? tier) => tier switch
    {
        "faithful" => ConversionTiers.Faithful,
        "structural" => ConversionTiers.Structural,
        "refused" => ConversionTiers.Refused,
        _ => ConversionTiers.NotOffered
    };
}
