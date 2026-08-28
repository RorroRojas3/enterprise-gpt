using System.Text;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Agents;

/// <summary>
/// Guards the published conversion matrix and the documentation table rendered from it.
/// </summary>
/// <remarks>
/// A JSON file alone does not make a single source: the moment someone renders it into prose, prose is
/// what the next reader believes. These tests require the published table to be character-for-character
/// what the JSON renders to, and print the correct block when it is not. No Azure, Docker or
/// credentials — the run that produces the data is billable, and the guard on it must not be.
/// </remarks>
public sealed class ConversionMatrixDocumentTests
{
    // Resolved against the assembly rather than the current directory. Both files arrive through
    // copy-to-output, and a runner that does not set the working directory to the output folder would
    // otherwise fail with a file-not-found that names neither of them.
    private static readonly string MatrixPath =
        Path.Combine(AppContext.BaseDirectory, "Agents", "Documents", "conversion-matrix.json");

    private static readonly string DocumentPath =
        Path.Combine(AppContext.BaseDirectory, "file-agent-sandbox-capabilities.md");

    // The agent reads this one, and the pair it offers has to be the pair the matrix confirmed —
    // a conversion refused in one place and offered in the other is the failure this catches.
    private static readonly string ConversionSkillPath = Path.Combine(
        AppContext.BaseDirectory, "Agents", "Documents", "Skills", "document-conversion", "SKILL.md");
    private const string BeginMarker = "<!-- conversion-matrix:begin -->";
    private const string EndMarker = "<!-- conversion-matrix:end -->";

    private static readonly string[] Tiers = ["faithful", "structural", "refused", "notOffered"];
    private static readonly string[] Statuses = ["proposed", "confirmed"];

    [Fact]
    public void ConversionMatrix_EveryFormatPair_HasExactlyOneCell()
    {
        using var matrix = Load();
        var formats = Formats(matrix);
        var cells = Cells(matrix);

        var expected = formats
            .SelectMany(from => formats.Where(to => to != from).Select(to => (from, to)))
            .ToList();

        var actual = cells
            .Select(cell => (cell.GetProperty("from").GetString()!, cell.GetProperty("to").GetString()!))
            .ToHashSet();

        var missing = expected.Where(pair => !actual.Contains(pair)).ToList();

        Assert.True(
            missing.Count == 0,
            "A pair with no cell is a pair the agent has no answer for, which reaches a user as silence "
            + $"rather than a refusal. Missing: {string.Join(", ", missing.Select(pair => $"{pair.from} to {pair.to}"))}.");

        Assert.Equal(expected.Count, cells.Count);

        Assert.DoesNotContain(cells, cell =>
            cell.GetProperty("from").GetString() == cell.GetProperty("to").GetString());
    }

    [Fact]
    public void ConversionMatrix_EveryCell_CarriesAKnownTierAndANote()
    {
        using var matrix = Load();

        foreach (var cell in Cells(matrix))
        {
            var pair = $"{cell.GetProperty("from").GetString()} to {cell.GetProperty("to").GetString()}";

            Assert.Contains(cell.GetProperty("tier").GetString(), Tiers);
            Assert.Contains(cell.GetProperty("proposedTier").GetString(), Tiers);

            // Rendering the published table reads a glyph per tier, so a tier with none is a table
            // that cannot be produced at all - checked here rather than surfacing as a bare
            // KeyNotFoundException out of the renderer.
            Assert.True(
                matrix.RootElement.GetProperty("tierGlyphs").TryGetProperty(cell.GetProperty("tier").GetString()!, out _),
                $"The cell for {pair} carries tier '{cell.GetProperty("tier").GetString()}', which tierGlyphs has no glyph for.");

            // The note is what the answer must tell the user about this pair, so an empty one on a
            // structural cell is a conversion that silently drops its caveat.
            Assert.False(
                string.IsNullOrWhiteSpace(cell.GetProperty("note").GetString()),
                $"The cell for {pair} carries no note, so nothing tells the answer what to say about it.");
        }
    }

    [Fact]
    public void ConversionMatrix_ConfirmationAndEvidence_AgreeWithEachOther()
    {
        using var matrix = Load();
        var status = matrix.RootElement.GetProperty("status").GetString();

        Assert.Contains(status, Statuses);

        foreach (var cell in Cells(matrix))
        {
            var pair = $"{cell.GetProperty("from").GetString()} to {cell.GetProperty("to").GetString()}";
            var confirmed = cell.GetProperty("confirmed").GetBoolean();
            var hasEvidence = cell.GetProperty("evidence").ValueKind is not JsonValueKind.Null;

            Assert.True(
                confirmed == hasEvidence,
                $"The cell for {pair} says confirmed={confirmed} but {(hasEvidence ? "carries" : "carries no")} "
                + "evidence. A tier without the attempt that settled it is a claim, and this file exists "
                + "so that tiers are not claims.");

            if (status == "confirmed" && cell.GetProperty("proposedTier").GetString() != "notOffered")
            {
                Assert.True(confirmed, $"The matrix is marked confirmed, but {pair} was never attempted.");
            }
        }
    }

    [Fact]
    public void PublishedDocument_MatrixTable_IsExactlyWhatTheJsonRendersTo()
    {
        using var matrix = Load();

        var document = File.ReadAllText(DocumentPath);
        var start = document.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = document.IndexOf(EndMarker, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, $"'{DocumentPath}' carries no rendered matrix block.");

        var published = document[(start + BeginMarker.Length)..end].ReplaceLineEndings("\n").Trim('\n');
        var rendered = Render(matrix);

        Assert.True(
            published == rendered,
            $"The matrix table in '{DocumentPath}' no longer matches '{MatrixPath}'. Replace everything "
            + $"between the markers with:{Environment.NewLine}{Environment.NewLine}{rendered}");
    }

    [Fact]
    public void ConversionSkill_MatrixTable_IsExactlyWhatTheJsonRendersTo()
    {
        using var matrix = Load();

        Assert.True(File.Exists(ConversionSkillPath), $"'{ConversionSkillPath}' did not ship with the build.");

        var rendered = Render(matrix);

        Assert.True(
            ReadMatrixBlock(ConversionSkillPath) == rendered,
            $"The matrix table in '{ConversionSkillPath}' no longer matches '{MatrixPath}'. Replace everything "
            + $"between the markers with:{Environment.NewLine}{Environment.NewLine}{rendered}");
    }

    private static string ReadMatrixBlock(string path)
    {
        var document = File.ReadAllText(path);
        var start = document.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = document.IndexOf(EndMarker, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, $"'{path}' carries no rendered matrix block.");

        return document[(start + BeginMarker.Length)..end].ReplaceLineEndings("\n").Trim('\n');
    }

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllText(MatrixPath));

    private static List<string> Formats(JsonDocument matrix) =>
        [.. matrix.RootElement.GetProperty("formats").EnumerateArray().Select(format => format.GetString()!)];

    private static List<JsonElement> Cells(JsonDocument matrix) =>
        [.. matrix.RootElement.GetProperty("cells").EnumerateArray()];

    private static string Render(JsonDocument matrix)
    {
        var root = matrix.RootElement;
        var formats = Formats(matrix);
        var glyphs = root.GetProperty("tierGlyphs");

        var tiers = Cells(matrix).ToDictionary(
            cell => (cell.GetProperty("from").GetString()!, cell.GetProperty("to").GetString()!),
            cell => glyphs.GetProperty(cell.GetProperty("tier").GetString()!).GetString()!);

        var builder = new StringBuilder()
            .Append("Status: ").Append(root.GetProperty("status").GetString())
            .Append(". Recorded: ").Append(Recorded(root))
            .Append(". Deployment: ").Append(Text(root, "deployment", "none yet"))
            .Append(". Office converter: ").Append(OfficeConverter(root))
            .Append(".\n\n")
            .Append(@"| From \ To | ").Append(string.Join(" | ", formats)).Append(" |\n")
            .Append("| --- |").Append(string.Concat(Enumerable.Repeat(" --- |", formats.Count))).Append('\n');

        foreach (var from in formats)
        {
            builder.Append("| ").Append(from).Append(" | ")
                .Append(string.Join(" | ", formats.Select(to => from == to ? "\u2014" : tiers[(from, to)])))
                .Append(" |\n");
        }

        return builder.ToString().TrimEnd('\n');
    }

    // Rendered as a plain date. The serialized instant carries sub-second precision that reads as
    // noise in a published table and would have to be transcribed exactly to keep this test green.
    private static string Recorded(JsonElement root) =>
        root.GetProperty("recordedUtc").ValueKind is JsonValueKind.Null
            ? "never"
            : root.GetProperty("recordedUtc").GetDateTimeOffset().ToString("yyyy-MM-dd");

    private static string OfficeConverter(JsonElement root) =>
        root.GetProperty("officeConverterPresent").ValueKind switch
        {
            JsonValueKind.True => "present",
            JsonValueKind.False => "absent",
            _ => "unknown",
        };

    private static string Text(JsonElement root, string property, string fallback) =>
        root.GetProperty(property).GetString() is { Length: > 0 } value ? value : fallback;
}
