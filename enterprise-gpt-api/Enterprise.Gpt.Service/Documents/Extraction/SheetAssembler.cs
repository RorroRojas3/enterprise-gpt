using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Documents.Chunking;
using Enterprise.Gpt.Service.Documents.Extraction.Models;
using Enterprise.Gpt.Service.Options;

namespace Enterprise.Gpt.Service.Documents.Extraction;

/// <summary>
/// Turns one sheet's rows into a schema card, header-repeating row windows, and the columns and rows
/// persisted behind them — the single implementation both spreadsheet formats go through.
/// </summary>
/// <remarks>
/// Windows are shaped here, not in the chunker: one within budget survives as a single chunk with its
/// header, and one over budget splits on <c>\n</c>, i.e. between rows.
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

    // Relaxed: cells never render into markup, and a column read in SQL Server should show its text,
    // not \uXXXX escapes.
    private static readonly JsonWriterOptions _cellWriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    #region Private methods

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

    // Same token budget as a row window: a wide sheet lists the columns that fit and counts the rest.
    // Queries resolve names against the persisted columns, not this.
    private static string BuildSchemaCard(
        string sheetName,
        IReadOnlyList<string[]> rows,
        int firstDataRow,
        int rowCount,
        string[] columnNames,
        SheetColumnTypes[] columnTypes,
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

        // All or nothing: a heading with nothing under it describes nothing.
        if (included > 0)
        {
            card.Append(SampleHeading).Append(headerText).Append(samples);
        }

        return card.ToString();
    }

    // All text, except bare years (budget exports head columns with them). Strict on purpose: a data row
    // taken for a heading silently drops from every total.
    private static bool IsHeaderRow(string[] cells)
    {
        var sawText = false;

        foreach (var cell in cells)
        {
            if (cell.Length == 0)
            {
                continue;
            }

            if (Classify(cell) == SheetColumnTypes.Text)
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

            // A name resolving to two columns is unanswerable, so repeated headings are disambiguated.
            while (!used.Add(name))
            {
                var tail = string.Create(CultureInfo.InvariantCulture, $" ({suffix++})");
                name = SheetSegmentBuilder.TruncateName(declared, SheetSegmentBuilder.MaxNameLength - tail.Length) + tail;
            }

            names[i] = name;
        }

        return names;
    }

    private static SheetColumnTypes[] InferColumnTypes(IReadOnlyList<string[]> rows, int firstDataRow, int columnCount)
    {
        var candidates = new SheetColumnTypes?[columnCount];
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
                candidates[c] = candidates[c] is { } existing && existing != kind ? SheetColumnTypes.Text : kind;
            }
        }

        var types = new SheetColumnTypes[columnCount];

        for (var c = 0; c < columnCount; c++)
        {
            types[c] = candidates[c] ?? SheetColumnTypes.Text;
        }

        return types;
    }

    private static SheetColumnTypes Classify(string value)
    {
        if (bool.TryParse(value, out _))
        {
            return SheetColumnTypes.Boolean;
        }

        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
        {
            return SheetColumnTypes.Number;
        }

        // Exactly the formats extraction renders dates as; a loose parse would read order codes as dates.
        return DateTime.TryParseExact(value, _dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? SheetColumnTypes.Date
            : SheetColumnTypes.Text;
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

    private static string TypeName(SheetColumnTypes type)
    {
        return type switch
        {
            SheetColumnTypes.Number => "number",
            SheetColumnTypes.Date => "date",
            SheetColumnTypes.Boolean => "boolean",
            _ => "text"
        };
    }

    #endregion

    #region Public static methods

    /// <param name="sheetName">Already normalized and truncated by the caller.</param>
    /// <param name="sheetIndex">1-based; every segment's source number.</param>
    /// <param name="rows">Populated rows in reading order, each trimmed to its last populated cell.</param>
    /// <param name="options">Row-window limits.</param>
    /// <param name="chunker">Sizes the schema card and row windows.</param>
    /// <param name="cellBudget">Serialized cell characters still available to this upload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

        // Past half the budget a prefix leaves one row per window. The schema card already carries the
        // header and name, so the prefix gives way first (a name can tokenize densely in some scripts).
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
            // Per row: a sheet at the row ceiling is a long CPU-bound stretch, and the caller checks only
            // between sheets.
            cancellationToken.ThrowIfCancellationRequested();

            var row = rows[i];

            scratch.Clear();
            SheetSegmentBuilder.AppendRow(scratch, row, row.Length - 1);
            var rowText = scratch.ToString();
            var rowTokens = chunker.CountTokens(rowText);

            // Closed only once it holds a row, so an over-budget row gets its own window instead of stalling.
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

            // Each populated cell repeats its column name, so the stored form outgrows the text, and all
            // of it stays resident until the document is saved.
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

    #endregion
}
