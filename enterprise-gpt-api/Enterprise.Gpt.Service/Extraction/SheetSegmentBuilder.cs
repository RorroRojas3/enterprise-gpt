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
    internal const string SheetPrefix = "Sheet: ";

    /// <summary>
    /// Longest sheet or column name persisted, matching the column both are stored in.
    /// </summary>
    public const int MaxNameLength = 256;

    private const string CellSeparator = " | ";

    private static readonly SearchValues<char> _cellBreaks = SearchValues.Create("\r\n\t");

    public static void AppendSheetName(StringBuilder builder, string sheetName)
    {
        builder.Append(SheetPrefix).Append(sheetName).Append('\n');
    }

    /// <summary>
    /// Cuts a sheet or column name to what its column holds, never inside a surrogate pair.
    /// </summary>
    /// <remarks>
    /// A workbook's own sheet names are attacker-controlled — Excel caps them at 31 characters but
    /// the format does not — and an over-long one would fail at the insert rather than on read.
    /// </remarks>
    public static string TruncateName(string value, int length = MaxNameLength)
    {
        if (value.Length <= length)
        {
            return value;
        }

        return value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];
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
    /// Rendered length of a row, matching what <see cref="AppendRow"/> writes for the same cells.
    /// </summary>
    public static int RowLength(IReadOnlyList<string> cells, int lastIndex)
    {
        var length = 1;

        for (var i = 0; i <= lastIndex; i++)
        {
            length += cells[i].Length;
        }

        return length + (lastIndex * CellSeparator.Length);
    }

    /// <summary>
    /// Rendered length of the line <see cref="AppendSheetName"/> writes.
    /// </summary>
    public static int SheetHeaderLength(string sheetName)
    {
        return SheetPrefix.Length + sheetName.Length + 1;
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

    public static ValidationException CellLimitExceeded(int maxCharacters)
    {
        return Refuse($"The spreadsheet's cells expand to more than the {maxCharacters:N0} characters an upload accepts once stored under their column names. Split it into smaller files and try again.");
    }

    public static ValidationException UploadRowLimitExceeded(int maxRows)
    {
        return Refuse($"The spreadsheet holds more than the {maxRows:N0} rows an upload accepts across all of its sheets. Split it into smaller files and try again.");
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
