using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Settings;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Enterprise.Gpt.Service.Extraction;

/// <summary>
/// The segments a sheet is retrieved through and the structured grid behind them.
/// </summary>
/// <param name="CellCharacters">Length of the serialized cell data, to charge against the upload's budget.</param>
internal sealed record SheetAssembly(
    IReadOnlyList<DocumentSegmentDto> Segments,
    SheetStructureDto Structure,
    int CellCharacters);

/// <summary>
/// Turns one sheet's rows into a schema card, header-repeating row windows, and the columns and rows
/// persisted behind them — the single implementation both spreadsheet formats go through.
/// </summary>
/// <remarks>
/// A window is shaped here rather than the chunker taught about tables: the chunker splits a segment
/// on a blank line first, so a window that fits its token budget survives as one indivisible chunk and
/// carries its header, and one that does not falls back to the sentence boundary, which breaks on
/// <c>\n</c> and therefore on row boundaries rather than inside a row.
/// </remarks>
internal static class SheetAssembler
{
    // Deep enough to characterize a column without walking a sheet at its row ceiling twice.
    private const int TypeSampleRows = 200;

    private const int SampleRowsInCard = 3;

    private const string SampleHeading = "Sample rows:\n";

    private static readonly string[] _dateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm:ss",
        "HH:mm:ss"
    ];

    // Relaxed rather than the default encoder: nothing renders these cells into markup, and a column
    // read directly in SQL Server should show its text instead of a run of \uXXXX escapes.
    private static readonly JsonWriterOptions _cellWriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Assembles one sheet.
    /// </summary>
    /// <param name="rows">
    /// The sheet's populated rows in reading order, each trimmed to its own last populated cell.
    /// </param>
    /// <param name="cellBudget">Serialized cell characters still available to this upload.</param>
    public static SheetAssembly Assemble(
        string sheetName,
        int sheetIndex,
        IReadOnlyList<string[]> rows,
        RowWindowOptions options,
        ITextChunker chunker,
        int cellBudget,
        CancellationToken cancellationToken)
    {
        var columnCount = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            columnCount = Math.Max(columnCount, rows[i].Length);
        }

        // A lone row is data: there is nothing for it to be the header of.
        var header = rows.Count > 1 && IsHeaderRow(rows[0]) ? rows[0] : null;
        var firstDataRow = header is null ? 0 : 1;
        var rowCount = rows.Count - firstDataRow;

        var columnNames = BuildColumnNames(header, columnCount);
        var columnTypes = InferColumnTypes(rows, firstDataRow, columnCount);
        var budget = Math.Max(1, (int)(chunker.MaxTokens * options.TargetTokenFraction));

        var scratch = new StringBuilder();

        List<DocumentSegmentDto> segments =
        [
            Segment(sheetIndex, BuildSchemaCard(sheetName, rows, firstDataRow, rowCount, columnNames, columnTypes, header, budget, chunker, scratch))
        ];

        var prefix = BuildWindowPrefix(sheetName, header, scratch);
        var prefixTokens = chunker.CountTokens(prefix);

        // Anything repeated onto every window costs the budget it takes; past half of it a window is
        // mostly prefix and holds one row. The schema card carries both the name and the columns, so
        // the prefix gives way rather than the data — the sheet line too, since a name is bounded in
        // characters and a script that tokenizes several to the character can still crowd out a row.
        if (prefixTokens * 2 > budget)
        {
            prefix = BuildWindowPrefix(sheetName, header: null, scratch);
            prefixTokens = chunker.CountTokens(prefix);
        }

        if (prefixTokens * 2 > budget)
        {
            prefix = string.Empty;
            prefixTokens = 0;
        }

        var window = new StringBuilder(prefix);
        var windowTokens = prefixTokens;
        var windowRows = 0;

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, _cellWriterOptions);
        var structuredRows = new List<SheetRowDto>(rowCount);
        var cellCharacters = 0;

        for (var i = firstDataRow; i < rows.Count; i++)
        {
            // Per row, not per sheet: counting tokens and growing the window over a sheet at the row
            // ceiling is a long CPU-bound stretch, and the caller only checks between sheets.
            cancellationToken.ThrowIfCancellationRequested();

            var row = rows[i];

            scratch.Clear();
            SheetSegmentBuilder.AppendRow(scratch, row, row.Length - 1);
            var rowText = scratch.ToString();
            var rowTokens = chunker.CountTokens(rowText);

            // Only ever closed on a window that already holds a row, so a single row larger than the
            // budget still gets a window of its own rather than stalling the loop.
            if (windowRows > 0 && (windowRows == options.MaxRows || windowTokens + rowTokens > budget))
            {
                segments.Add(Segment(sheetIndex, window.ToString()));
                window.Clear().Append(prefix);
                windowTokens = prefixTokens;
                windowRows = 0;
            }

            window.Append(rowText);
            windowTokens += rowTokens;
            windowRows++;

            var cells = SerializeCells(row, columnNames, buffer, writer);
            cellCharacters += cells.Length;

            // Every populated cell carries its column's name, so a wide sheet's stored form outgrows
            // the text it was read from — and all of it is resident until the document is saved.
            if (cellCharacters > cellBudget)
            {
                throw SheetSegmentBuilder.CellLimitExceeded(cellBudget);
            }

            structuredRows.Add(new SheetRowDto(i - firstDataRow, cells));
        }

        if (windowRows > 0)
        {
            segments.Add(Segment(sheetIndex, window.ToString()));
        }

        List<SheetColumnDto> columns = [.. columnNames.Select((name, index) => new SheetColumnDto(index, name, columnTypes[index]))];

        return new SheetAssembly(
            segments,
            new SheetStructureDto(sheetIndex, sheetName, rowCount, columnCount, columns, structuredRows),
            cellCharacters);
    }

    private static DocumentSegmentDto Segment(int sheetIndex, string text)
    {
        return new DocumentSegmentDto { SourceNumber = sheetIndex, Text = text.TrimEnd() };
    }

    private static string BuildWindowPrefix(string sheetName, string[]? header, StringBuilder scratch)
    {
        scratch.Clear();
        SheetSegmentBuilder.AppendSheetName(scratch, sheetName);

        if (header is not null)
        {
            SheetSegmentBuilder.AppendRow(scratch, header, header.Length - 1);
        }

        return scratch.ToString();
    }

    /// <summary>
    /// Builds the one segment per sheet that describes the sheet rather than carrying its data.
    /// </summary>
    /// <remarks>
    /// Held to the same token budget as a row window, so a sheet at the column ceiling names as many
    /// columns as fit and says how many it left out; the persisted column rows, not this, are what a
    /// query resolves a name against.
    /// </remarks>
    private static string BuildSchemaCard(
        string sheetName,
        IReadOnlyList<string[]> rows,
        int firstDataRow,
        int rowCount,
        string[] columnNames,
        SheetColumnType[] columnTypes,
        string[]? header,
        int budget,
        ITextChunker chunker,
        StringBuilder scratch)
    {
        var opening = string.Create(CultureInfo.InvariantCulture, $"{SheetSegmentBuilder.SheetPrefix}{sheetName}\nColumns: ");
        var card = new StringBuilder(opening);

        var used = chunker.CountTokens(opening);
        var listed = 0;

        for (var i = 0; i < columnNames.Length; i++)
        {
            scratch.Clear();

            if (i > 0)
            {
                scratch.Append(", ");
            }

            scratch.Append(columnNames[i]).Append(" (").Append(TypeName(columnTypes[i])).Append(')');
            var entry = scratch.ToString();
            var entryTokens = chunker.CountTokens(entry);

            if (i > 0 && used + entryTokens > budget)
            {
                break;
            }

            card.Append(entry);
            used += entryTokens;
            listed++;
        }

        if (listed < columnNames.Length)
        {
            var omitted = string.Create(CultureInfo.InvariantCulture, $", and {columnNames.Length - listed} more columns");
            card.Append(omitted);
            used += chunker.CountTokens(omitted);
        }

        var counts = string.Create(CultureInfo.InvariantCulture, $"\nRows: {rowCount}\n");
        card.Append(counts);
        used += chunker.CountTokens(counts);

        var headerText = string.Empty;

        if (header is not null)
        {
            scratch.Clear();
            SheetSegmentBuilder.AppendRow(scratch, header, header.Length - 1);
            headerText = scratch.ToString();
        }

        var samples = new StringBuilder();
        var sampleTokens = chunker.CountTokens(SampleHeading) + chunker.CountTokens(headerText);
        var included = 0;

        for (var i = 0; i < Math.Min(SampleRowsInCard, rowCount); i++)
        {
            var row = rows[firstDataRow + i];

            scratch.Clear();
            SheetSegmentBuilder.AppendRow(scratch, row, row.Length - 1);
            var rowText = scratch.ToString();
            var rowTokens = chunker.CountTokens(rowText);

            if (used + sampleTokens + rowTokens > budget)
            {
                break;
            }

            samples.Append(rowText);
            sampleTokens += rowTokens;
            included++;
        }

        // All or nothing: a sheet whose rows are too wide to illustrate is already described by the
        // column list above, and a heading with nothing under it describes nothing.
        if (included > 0)
        {
            card.Append(SampleHeading).Append(headerText).Append(samples);
        }

        return card.ToString();
    }

    /// <summary>
    /// Whether the first row reads as column headings rather than data.
    /// </summary>
    /// <remarks>
    /// Text throughout, with a bare year the one typed exception — a budget export heads its measure
    /// columns with years and nothing else numeric reads as a heading. Erring the other way is the
    /// costlier mistake: a data row taken for a heading disappears from every total silently, where a
    /// heading taken for data at least surfaces as columns nobody can name.
    /// </remarks>
    private static bool IsHeaderRow(string[] cells)
    {
        var sawText = false;

        foreach (var cell in cells)
        {
            if (cell.Length == 0)
            {
                continue;
            }

            if (Classify(cell) == SheetColumnType.Text)
            {
                sawText = true;
                continue;
            }

            if (!IsYearLike(cell))
            {
                return false;
            }
        }

        return sawText;
    }

    private static bool IsYearLike(string value)
    {
        return value.Length == 4
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1900 and <= 2199;
    }

    /// <summary>
    /// Names every column, uniquely, whether or not the sheet declared headings.
    /// </summary>
    private static string[] BuildColumnNames(string[]? header, int columnCount)
    {
        var names = new string[columnCount];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < columnCount; i++)
        {
            var declared = header is not null && i < header.Length && header[i].Length > 0
                ? SheetSegmentBuilder.TruncateName(header[i])
                : string.Create(CultureInfo.InvariantCulture, $"Column{i + 1}");

            var name = declared;
            var suffix = 2;

            // A name that resolves to two columns is a question nothing can answer, so a repeated
            // heading is disambiguated here rather than left for a caller to discover.
            while (!used.Add(name))
            {
                var tail = string.Create(CultureInfo.InvariantCulture, $" ({suffix++})");
                name = SheetSegmentBuilder.TruncateName(declared, SheetSegmentBuilder.MaxNameLength - tail.Length) + tail;
            }

            names[i] = name;
        }

        return names;
    }

    private static SheetColumnType[] InferColumnTypes(IReadOnlyList<string[]> rows, int firstDataRow, int columnCount)
    {
        var candidates = new SheetColumnType?[columnCount];
        var sampled = Math.Min(rows.Count, firstDataRow + TypeSampleRows);

        for (var r = firstDataRow; r < sampled; r++)
        {
            var row = rows[r];

            for (var c = 0; c < row.Length; c++)
            {
                if (row[c].Length == 0)
                {
                    continue;
                }

                var kind = Classify(row[c]);
                candidates[c] = candidates[c] is { } existing && existing != kind ? SheetColumnType.Text : kind;
            }
        }

        var types = new SheetColumnType[columnCount];

        for (var c = 0; c < columnCount; c++)
        {
            types[c] = candidates[c] ?? SheetColumnType.Text;
        }

        return types;
    }

    private static SheetColumnType Classify(string value)
    {
        if (bool.TryParse(value, out _))
        {
            return SheetColumnType.Boolean;
        }

        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
        {
            return SheetColumnType.Number;
        }

        // Exactly the formats extraction renders a date cell as, and no others: a loose parse would
        // read an order code or a version string as a date.
        return DateTime.TryParseExact(value, _dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? SheetColumnType.Date
            : SheetColumnType.Text;
    }

    private static string SerializeCells(string[] row, string[] columnNames, ArrayBufferWriter<byte> buffer, Utf8JsonWriter writer)
    {
        buffer.Clear();
        writer.Reset(buffer);
        writer.WriteStartObject();

        for (var i = 0; i < row.Length; i++)
        {
            if (row[i].Length == 0)
            {
                continue;
            }

            writer.WriteString(columnNames[i], row[i]);
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string TypeName(SheetColumnType type)
    {
        return type switch
        {
            SheetColumnType.Number => "number",
            SheetColumnType.Date => "date",
            SheetColumnType.Boolean => "boolean",
            _ => "text"
        };
    }
}
