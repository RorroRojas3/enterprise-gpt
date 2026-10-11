using System.Text.Json;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Agents.Models;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>The confirmed source-to-target conversion matrix.</summary>
/// <remarks>
/// The <c>document-conversion</c> skill's table and the run's pre-flight both read it, so they cannot disagree.
/// </remarks>
public interface IConversionMatrix
{
    /// <summary>Gets the formats the matrix covers, in its own published order.</summary>
    IReadOnlyList<string> Formats { get; }

    /// <summary>Gets every cell, one per ordered pair of distinct formats.</summary>
    IReadOnlyList<ConversionCell> Cells { get; }

    /// <summary>Finds the cell for a pair of formats, each with or without a leading dot.</summary>
    /// <returns>The cell, or <see langword="null"/> when either format is not covered.</returns>
    ConversionCell? Find(string? from, string? to);

    /// <summary>Lists the faithful and structural targets a source can reach, in published order.</summary>
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

    #region Private methods

    /// <summary>Lower-cases a format and drops a leading dot, so ".DOCX" and "docx" are one key.</summary>
    private static string Normalize(string? format) =>
        format is null ? string.Empty : format.Trim().TrimStart('.').ToLowerInvariant();

    // An unknown tier reads as notOffered rather than throwing, so a newer matrix cannot take the agent down.
    private static ConversionTiers ParseTier(string? tier) => tier switch
    {
        "faithful" => ConversionTiers.Faithful,
        "structural" => ConversionTiers.Structural,
        "refused" => ConversionTiers.Refused,
        _ => ConversionTiers.NotOffered
    };

    #endregion

    #region Public static methods

    /// <summary>Reads the published matrix off disk, from <see cref="DeployedPath"/> by default.</summary>
    /// <exception cref="FileNotFoundException">The matrix did not ship with this build.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is malformed, incomplete, has a duplicated pair or no cells; one type so the startup failure
    /// names the file.
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
        // A well-formed document of the wrong shape raises InvalidOperationException, not JsonException.
        catch (Exception exception)
            when (exception is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"The conversion matrix at '{file}' could not be read.", exception);
        }
    }

    #endregion
}
