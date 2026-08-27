using FluentValidation;
using FluentValidation.Results;
using Enterprise.Gpt.Dto;
using System.Buffers;
using System.Text;

namespace Enterprise.Gpt.Service.Extraction;

/// <summary>
/// Shared text shaping and refusal messages for the spreadsheet extractors, so <c>.xlsx</c> and
/// <c>.csv</c> produce byte-identical segment text for the same grid of cells.
/// </summary>
internal static class SheetSegmentBuilder
{
    // Pipes rather than tabs because the only consumer of this text is a model reading a retrieved
    // passage; nothing parses it back, so legibility is the whole criterion.
    private const string CellSeparator = " | ";

    private static readonly SearchValues<char> _cellBreaks = SearchValues.Create("\r\n\t");

    public static StringBuilder StartSheet(string sheetName)
    {
        return new StringBuilder().Append("Sheet: ").Append(sheetName).Append('\n');
    }

    /// <summary>
    /// Returns the index of the last populated cell, or -1 when the row is entirely empty.
    /// </summary>
    public static int LastPopulatedIndex(IReadOnlyList<string> cells)
    {
        for (var i = cells.Count - 1; i >= 0; i--)
        {
            if (cells[i].Length > 0)
            {
                return i;
            }
        }

        return -1;
    }

    public static void AppendRow(StringBuilder builder, IReadOnlyList<string> cells, int lastIndex)
    {
        for (var i = 0; i <= lastIndex; i++)
        {
            if (i > 0)
            {
                builder.Append(CellSeparator);
            }

            builder.Append(cells[i]);
        }

        builder.Append('\n');
    }

    /// <summary>
    /// Trims a cell and folds any line break or tab inside it into a single space.
    /// </summary>
    /// <remarks>
    /// One line is one row for everything downstream — the chunker's sentence boundary breaks on
    /// <c>\n</c> — so a cell carrying its own newline would split its row across two chunks.
    /// </remarks>
    public static string NormalizeCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().ContainsAny(_cellBreaks)
            ? FoldBreaks(value)
            : value.Trim();
    }

    public static ValidationException RowLimitExceeded(string sheetName, int maxRows)
    {
        return Refuse($"Sheet '{sheetName}' has more than the {maxRows:N0} rows a spreadsheet upload accepts. Split it into smaller files and try again.");
    }

    public static ValidationException ColumnLimitExceeded(string sheetName, int maxColumns)
    {
        return Refuse($"Sheet '{sheetName}' has more than the {maxColumns:N0} columns a spreadsheet upload accepts. Remove the extra columns and try again.");
    }

    public static ValidationException TextLimitExceeded(int maxCharacters)
    {
        return Refuse($"The spreadsheet holds more than the {maxCharacters:N0} characters of cell text an upload accepts. Split it into smaller files and try again.");
    }

    public static ValidationException PackageLimitExceeded(long maxBytes)
    {
        var size = maxBytes >= 1024 * 1024 ? $"{maxBytes / (1024 * 1024):N0} MB" : $"{maxBytes / 1024:N0} KB";

        return Refuse($"The workbook expands to more than the {size} an upload accepts once decompressed. Split it into smaller files and try again.");
    }

    private static ValidationException Refuse(string message)
    {
        return new ValidationException([new ValidationFailure(nameof(FileDto.FileName), message)]);
    }

    private static string FoldBreaks(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var c in value)
        {
            if (c is '\r' or '\n' or '\t')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }
}
