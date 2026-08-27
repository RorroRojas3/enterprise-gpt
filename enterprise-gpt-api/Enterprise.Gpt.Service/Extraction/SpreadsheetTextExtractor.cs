using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Settings;
using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Enterprise.Gpt.Service.Extraction;

/// <summary>
/// Reads Excel (<c>.xlsx</c>) workbooks locally with the Open XML SDK, one segment per worksheet.
/// </summary>
/// <remarks>
/// A segment opens with the sheet's name and then carries one line per row. Every ceiling in
/// <see cref="SheetOptions"/> is enforced while streaming, because a workbook is a compressed archive
/// and its uncompressed size is not bounded by the accepted upload size.
/// </remarks>
public sealed class SpreadsheetTextExtractor(
    ILogger<SpreadsheetTextExtractor> logger,
    IOptions<SheetOptions> options) : IDocumentTextExtractor
{
    // DateTime.FromOADate's upper bound, which is 9999-12-31 in the 1900 date system.
    private const double MaxSerialDate = 2958465d;

    // Excel's 1900 serials include a 29 February 1900 that never existed, so the two calendars only
    // agree from 1 March 1900 (serial 61) onward.
    private const double FirstAgreedSerial = 60d;

    // The 1904 date system's epoch is this many days after the 1900 system's.
    private const int Date1904OffsetDays = 1462;

    // Excel's widest column reference is XFD, so a longer run of letters is malformed.
    private const int MaxColumnReferenceLetters = 3;

    // What a shared string costs beyond its own characters, so a table of empty entries is still bounded.
    private const int StringEntryOverhead = 16;

    // How much markup a character of cell text is allowed to arrive in. Element names, cell references
    // and style attributes dominate a worksheet, so the package inflates well past the text it carries.
    private const int MarkupBytesPerCharacter = 32;

    // Package boilerplate — content types, relationships, the workbook part — costs a few kilobytes
    // before a single cell is read, so the derived budget never falls below it.
    private const long MinimumPackageBytes = 256 * 1024;

    private const int InflateBufferSize = 64 * 1024;

    private readonly ILogger<SpreadsheetTextExtractor> _logger = logger;
    private readonly SheetOptions _options = options.Value;

    /// <inheritdoc />
    public IReadOnlyCollection<FileExtensions> SupportedExtensions { get; } = [FileExtensions.Xlsx];

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        // The whole parse is guarded, not just Open(). The Open XML SDK reads part XML lazily and parses
        // typed attributes on first access, so a truncated worksheet, a malformed style index, or a sheet
        // id pointing at a missing part all throw well after the package opens — and unguarded they would
        // surface as an opaque "unexpected error" job failure instead of a 400 the caller can act on.
        try
        {
            return ExtractSheets(file, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException
            or FileFormatException
            or InvalidDataException
            or XmlException
            or FormatException
            or OverflowException
            or ArgumentOutOfRangeException
            or KeyNotFoundException)
        {
            _logger.LogWarning(ex, "Failed to read an Excel workbook");
            throw new ValidationException(
            [
                new ValidationFailure(nameof(FileDto.FileName),
                    "The workbook could not be read. It may be corrupt or password-protected.")
            ]);
        }
    }

    private Task<IReadOnlyList<DocumentSegmentDto>> ExtractSheets(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        GuardPackageSize(file.Content, cancellationToken);

        using var stream = new MemoryStream(file.Content, writable: false);

        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("The package contains no workbook part.");

        // Sheets are enumerated through the workbook's own Sheets list rather than WorksheetParts
        // because only the former is in workbook order; part order is a package detail.
        var sheets = workbookPart.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? [];

        var sharedStrings = ReadSharedStrings(workbookPart, cancellationToken, out var characterBudget);
        var dateStyles = ReadDateStyles(workbookPart);
        var use1904 = workbookPart.Workbook?.WorkbookProperties?.Date1904?.Value == true;

        var segments = new List<DocumentSegmentDto>(sheets.Count);

        for (var i = 0; i < sheets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sheetNumber = i + 1;
            var relationshipId = sheets[i].Id?.Value;

            // A chart or dialog sheet resolves to something other than a WorksheetPart and has no
            // cell table, but it still consumes its ordinal so numbering matches the workbook.
            if (relationshipId is not null
                && workbookPart.GetPartById(relationshipId) is WorksheetPart worksheetPart)
            {
                var sheetName = ResolveSheetName(sheets[i], sheetNumber);
                var text = BuildSheetText(worksheetPart, sheetName, sharedStrings, dateStyles, use1904, characterBudget, cancellationToken);

                if (text.Length > 0)
                {
                    segments.Add(new DocumentSegmentDto { SourceNumber = sheetNumber, Text = text });
                    characterBudget -= text.Length;
                }
            }

            progress?.Report(new DocumentExtractionProgress(
                (double)sheetNumber / sheets.Count,
                $"Reading sheet {sheetNumber} of {sheets.Count}"));
        }

        _logger.LogInformation("Extracted text from {SegmentCount} of {SheetCount} sheet(s)", segments.Count, sheets.Count);

        return Task.FromResult<IReadOnlyList<DocumentSegmentDto>>(segments);
    }

    private string BuildSheetText(
        WorksheetPart worksheetPart,
        string sheetName,
        string[] sharedStrings,
        bool[] dateStyles,
        bool use1904,
        int characterBudget,
        CancellationToken cancellationToken)
    {
        var builder = SheetSegmentBuilder.StartSheet(sheetName);
        var cells = new List<string>();
        var rowCount = 0;
        var columnCursor = 0;

        // Cells are loaded one at a time rather than a row at a time: loading the row element would
        // materialize every cell in it before the column ceiling could refuse any of them, which is what
        // lets a few kilobytes of compressed markup cost hundreds of megabytes of heap.
        using var reader = OpenXmlReader.Create(worksheetPart);

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.ElementType == typeof(Row))
            {
                if (reader.IsStartElement)
                {
                    cells.Clear();
                    columnCursor = 0;
                }
                else
                {
                    AppendRow(builder, cells, sheetName, characterBudget, ref rowCount);
                }

                continue;
            }

            if (!reader.IsStartElement || reader.ElementType != typeof(Cell) || reader.LoadCurrentElement() is not Cell cell)
            {
                continue;
            }

            var columnIndex = GetColumnIndex(cell.CellReference?.Value, columnCursor);
            columnCursor = columnIndex + 1;

            // Resolved before the ceiling is applied, so a formatting-only cell far to the right — which
            // Excel persists routinely — does not refuse a workbook with three populated columns.
            var text = ResolveCellText(cell, sharedStrings, dateStyles, use1904);
            if (text.Length == 0)
            {
                continue;
            }

            if (columnIndex >= _options.MaxColumnsPerSheet)
            {
                throw SheetSegmentBuilder.ColumnLimitExceeded(sheetName, _options.MaxColumnsPerSheet);
            }

            while (cells.Count <= columnIndex)
            {
                cells.Add(string.Empty);
            }

            cells[columnIndex] = text;
        }

        return rowCount == 0 ? string.Empty : builder.ToString().TrimEnd();
    }

    private void AppendRow(
        StringBuilder builder,
        List<string> cells,
        string sheetName,
        int characterBudget,
        ref int rowCount)
    {
        var lastIndex = SheetSegmentBuilder.LastPopulatedIndex(cells);
        if (lastIndex < 0)
        {
            return;
        }

        if (rowCount == _options.MaxRowsPerSheet)
        {
            throw SheetSegmentBuilder.RowLimitExceeded(sheetName, _options.MaxRowsPerSheet);
        }

        SheetSegmentBuilder.AppendRow(builder, cells, lastIndex);
        rowCount++;

        if (builder.Length > characterBudget)
        {
            throw SheetSegmentBuilder.TextLimitExceeded(_options.MaxCharactersPerUpload);
        }
    }

    /// <summary>
    /// Refuses a package that inflates past its budget, before the SDK reads any of it.
    /// </summary>
    /// <remarks>
    /// A workbook is a deflate archive, so the accepted upload size bounds nothing on its own, and the
    /// SDK materializes an element before its content can be measured. Real inflated bytes are counted
    /// because a zip's declared entry length is attacker-controlled and never verified while inflating.
    /// </remarks>
    private void GuardPackageSize(byte[] content, CancellationToken cancellationToken)
    {
        var maxBytes = PackageBudget();
        var budget = maxBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(InflateBufferSize);

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                using var entryStream = entry.Open();

                int read;
                while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    budget -= read;
                    if (budget < 0)
                    {
                        throw SheetSegmentBuilder.PackageLimitExceeded(maxBytes);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private long PackageBudget()
    {
        return Math.Max(MinimumPackageBytes, (long)_options.MaxCharactersPerUpload * MarkupBytesPerCharacter);
    }

    private string[] ReadSharedStrings(WorkbookPart workbookPart, CancellationToken cancellationToken, out int remaining)
    {
        remaining = _options.MaxCharactersPerUpload;

        if (workbookPart.SharedStringTablePart is not { } part)
        {
            return [];
        }

        var values = new List<string>();

        using var reader = OpenXmlReader.Create(part);

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.IsStartElement
                && reader.ElementType == typeof(SharedStringItem)
                && reader.LoadCurrentElement() is SharedStringItem item)
            {
                // InnerText concatenates the runs a rich-text string is split into.
                var value = SheetSegmentBuilder.NormalizeCell(item.InnerText);

                remaining -= value.Length + StringEntryOverhead;
                if (remaining < 0)
                {
                    throw SheetSegmentBuilder.TextLimitExceeded(_options.MaxCharactersPerUpload);
                }

                values.Add(value);
            }
        }

        return [.. values];
    }

    /// <summary>
    /// Maps each cell-format index to whether it renders its value as a date or a time.
    /// </summary>
    private static bool[] ReadDateStyles(WorkbookPart workbookPart)
    {
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;

        if (stylesheet?.CellFormats is not { } cellFormats)
        {
            return [];
        }

        var customDateFormats = new HashSet<uint>();

        if (stylesheet.NumberingFormats is { } numberingFormats)
        {
            foreach (var format in numberingFormats.Elements<NumberingFormat>())
            {
                if (format.NumberFormatId?.Value is { } id
                    && format.FormatCode?.Value is { } code
                    && LooksLikeDateFormat(code))
                {
                    customDateFormats.Add(id);
                }
            }
        }

        var formats = cellFormats.Elements<CellFormat>().ToList();
        var isDate = new bool[formats.Count];

        for (var i = 0; i < formats.Count; i++)
        {
            var id = formats[i].NumberFormatId?.Value ?? 0u;
            isDate[i] = IsBuiltInDateFormat(id) || customDateFormats.Contains(id);
        }

        return isDate;
    }

    // The built-in date and time format ids are fixed by ECMA-376 rather than declared in the package.
    private static bool IsBuiltInDateFormat(uint numberFormatId)
    {
        return numberFormatId is (>= 14 and <= 22) or (>= 45 and <= 47);
    }

    private static bool LooksLikeDateFormat(string formatCode)
    {
        var inQuotes = false;
        var inBrackets = false;

        for (var i = 0; i < formatCode.Length; i++)
        {
            var c = formatCode[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes)
            {
                switch (c)
                {
                    case '[':
                        inBrackets = true;
                        continue;
                    case ']':
                        inBrackets = false;
                        continue;
                    case '\\':
                        i++;
                        continue;
                }
            }

            if (inQuotes || inBrackets)
            {
                continue;
            }

            if (c is 'y' or 'Y' or 'm' or 'M' or 'd' or 'D' or 'h' or 'H' or 's' or 'S')
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveCellText(Cell cell, string[] sharedStrings, bool[] dateStyles, bool use1904)
    {
        var value = cell.CellValue?.Text;

        if (cell.DataType is { } dataType)
        {
            if (dataType == CellValues.SharedString)
            {
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 0
                    && index < sharedStrings.Length
                        ? sharedStrings[index]
                        : string.Empty;
            }

            if (dataType == CellValues.InlineString)
            {
                return SheetSegmentBuilder.NormalizeCell(cell.InlineString?.InnerText);
            }

            if (dataType == CellValues.Boolean)
            {
                return value == "1" ? "TRUE" : "FALSE";
            }

            if (dataType == CellValues.Date)
            {
                return FormatIsoDate(value);
            }

            // String is a formula's cached text result and Error is a literal such as #DIV/0!; both are
            // already display text.
            if (dataType == CellValues.String || dataType == CellValues.Error)
            {
                return SheetSegmentBuilder.NormalizeCell(value);
            }
        }

        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // A number's stored text is already an invariant decimal string, so it is used verbatim rather
        // than parsed and reformatted.
        return IsDateStyled(cell, dateStyles)
            ? FormatSerialDate(value, use1904)
            : SheetSegmentBuilder.NormalizeCell(value);
    }

    private static bool IsDateStyled(Cell cell, bool[] dateStyles)
    {
        return cell.StyleIndex?.Value is { } index && index < (uint)dateStyles.Length && dateStyles[index];
    }

    private static string FormatSerialDate(string value, bool use1904)
    {
        var maxSerial = use1904 ? MaxSerialDate - Date1904OffsetDays : MaxSerialDate;

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            || !double.IsFinite(serial)
            || serial < 0
            || serial > maxSerial)
        {
            return SheetSegmentBuilder.NormalizeCell(value);
        }

        var date = DateTime.FromOADate(serial);

        // The 1904 system starts at serial 0, so even a sub-day value there is a real date.
        if (use1904)
        {
            return FormatDate(date.AddDays(Date1904OffsetDays));
        }

        // 1900 serials start at 1, so anything below that carries only a time of day.
        return serial < 1
            ? date.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : FormatDate(serial < FirstAgreedSerial ? date.AddDays(1) : date);
    }

    private static string FormatIsoDate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? FormatDate(date)
            : SheetSegmentBuilder.NormalizeCell(value);
    }

    private static string FormatDate(DateTime date)
    {
        return date.TimeOfDay == TimeSpan.Zero
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts a cell reference such as <c>C5</c> to its zero-based column index.
    /// </summary>
    /// <remarks>
    /// The SDK omits empty cells entirely, so without the reference a row with gaps would silently shift
    /// every later cell into the wrong column. Some generators omit the reference, which is what
    /// <paramref name="fallback"/> — the running position within the row — covers.
    /// </remarks>
    private static int GetColumnIndex(string? cellReference, int fallback)
    {
        if (string.IsNullOrEmpty(cellReference))
        {
            return fallback;
        }

        var index = 0;
        var letters = 0;

        foreach (var c in cellReference)
        {
            var upper = (char)(c & ~0x20);

            if (upper is < 'A' or > 'Z')
            {
                break;
            }

            index = (index * 26) + (upper - 'A' + 1);

            if (++letters == MaxColumnReferenceLetters)
            {
                break;
            }
        }

        return letters == 0 ? fallback : index - 1;
    }

    private static string ResolveSheetName(Sheet sheet, int sheetNumber)
    {
        var name = sheet.Name?.Value;

        return string.IsNullOrWhiteSpace(name)
            ? $"Sheet{sheetNumber}"
            : SheetSegmentBuilder.NormalizeCell(name);
    }
}
