using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Globalization;
using System.IO.Compression;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Builds minimal in-memory <c>.xlsx</c> packages for tests.
/// </summary>
/// <remarks>
/// Generating the fixture rather than checking in a binary keeps the expected text visible in the test
/// that asserts on it, and avoids a committed file nobody can diff.
/// </remarks>
public static class WorkbookBuilder
{
    /// <summary>Cell-format index carrying the built-in <c>m/d/yyyy</c> date format.</summary>
    public const uint BuiltInDateStyle = 1;

    /// <summary>Cell-format index carrying a custom date format declared in the package.</summary>
    public const uint CustomDateStyle = 2;

    /// <summary>Cell-format index carrying a custom format that is <em>not</em> a date.</summary>
    public const uint CustomNumberStyle = 3;

    /// <param name="Rows">
    /// Each entry is one row. A <see cref="string"/> becomes a shared string, a <see cref="double"/> or
    /// <see cref="int"/> a number, a <see cref="bool"/> a boolean, a <see cref="DateTime"/> a
    /// date-styled serial, and <see langword="null"/> is omitted entirely so the row carries a real gap.
    /// </param>
    public sealed record SheetSpec(string Name, IReadOnlyList<object?[]> Rows, bool Hidden = false);

    public sealed record InlineText(string Value);

    public sealed record Formula(string Expression, string CachedValue, bool IsText = false);

    public sealed record CellError(string Value);

    /// <summary>A value forced onto a particular cell-format index.</summary>
    public sealed record Styled(object? Value, uint StyleIndex);

    /// <summary>A shared-string cell whose index is written verbatim, valid or not.</summary>
    public sealed record RawSharedString(string Index);

    /// <summary>A cell whose attributes are written verbatim, so malformed ones can be tested.</summary>
    public sealed record RawAttributes(string Value, string? Reference = null, string? StyleIndex = null, string? DataType = null);

    public static byte[] Create(IReadOnlyList<SheetSpec> sheets, bool use1904 = false)
    {
        using var stream = new MemoryStream();

        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var sharedStrings = new List<string>();
            var sharedStringIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            var sheetElements = new Sheets();
            var sheetId = 1U;

            foreach (var spec in sheets)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                worksheetPart.Worksheet = new Worksheet(BuildSheetData(spec.Rows, sharedStrings, sharedStringIndexes));

                var sheet = new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = sheetId++,
                    Name = spec.Name
                };

                if (spec.Hidden)
                {
                    sheet.State = SheetStateValues.Hidden;
                }

                sheetElements.Append(sheet);
            }

            if (use1904)
            {
                workbookPart.Workbook.Append(new WorkbookProperties { Date1904 = true });
            }

            workbookPart.Workbook.Append(sheetElements);

            AddSharedStringTable(workbookPart, sharedStrings);
            AddStylesheet(workbookPart);

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates a workbook whose first sheet is a chart sheet, which resolves to no cell table.
    /// </summary>
    public static byte[] CreateWithChartSheetBefore(SheetSpec sheet)
    {
        using var stream = new MemoryStream();

        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var chartsheetPart = workbookPart.AddNewPart<ChartsheetPart>();
            chartsheetPart.Chartsheet = new Chartsheet(new SheetProperties());

            var sharedStrings = new List<string>();
            var sharedStringIndexes = new Dictionary<string, int>(StringComparer.Ordinal);

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(BuildSheetData(sheet.Rows, sharedStrings, sharedStringIndexes));

            workbookPart.Workbook.Append(new Sheets(
                new Sheet { Id = workbookPart.GetIdOfPart(chartsheetPart), SheetId = 1U, Name = "Chart" },
                new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 2U, Name = sheet.Name }));

            AddSharedStringTable(workbookPart, sharedStrings);
            AddStylesheet(workbookPart);

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates a workbook whose stylesheet carries number-format ids that are not numbers.
    /// </summary>
    public static byte[] CreateWithMalformedNumberFormats(string numberFormatId, string cellFormatId)
    {
        using var stream = new MemoryStream();

        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var row = new Row { RowIndex = 1U };
            row.Append(new Cell { CellReference = "A1", StyleIndex = 0U, CellValue = new CellValue("1") });
            worksheetPart.Worksheet = new Worksheet(new SheetData(row));

            workbookPart.Workbook.Append(new Sheets(
                new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1U, Name = "Data" }));

            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = new Stylesheet(
                new NumberingFormats(new NumberingFormat
                {
                    NumberFormatId = new UInt32Value { InnerText = numberFormatId },
                    FormatCode = "dd/mm/yyyy"
                }),
                new CellFormats(new CellFormat { NumberFormatId = new UInt32Value { InnerText = cellFormatId } }));
            stylesPart.Stylesheet.Save();

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates a package whose <c>Sheets</c> list references a relationship id that has no matching part.
    /// </summary>
    /// <remarks>
    /// The package opens without complaint; the fault only surfaces when the relationship is
    /// dereferenced, which is what makes it a test of exception handling around the whole parse rather
    /// than around <c>Open()</c>.
    /// </remarks>
    public static byte[] CreateWithDanglingSheetRelationship()
    {
        using var stream = new MemoryStream();

        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(
                new Sheets(new Sheet { Id = "rIdDoesNotExist", SheetId = 1U, Name = "Ghost" }));
            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates a package whose worksheet part contains malformed XML, which the SDK only rejects when it
    /// lazily parses that part.
    /// </summary>
    public static byte[] CreateWithCorruptSheetXml()
    {
        var content = Create([new SheetSpec("Data", [["Placeholder"]])]);

        using var stream = new MemoryStream();
        stream.Write(content);
        stream.Position = 0;

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var sheetEntry = archive.Entries.First(e => e.FullName.Contains("worksheets/sheet", StringComparison.Ordinal));

            using var entryStream = sheetEntry.Open();
            entryStream.SetLength(0);

            var malformed = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"u8;
            entryStream.Write(malformed);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates bytes shaped like a password-protected workbook, which Excel writes as an OLE2 compound
    /// file wrapping the encrypted package rather than as a zip.
    /// </summary>
    public static byte[] CreateEncryptedLikeWorkbook()
    {
        return [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[512]];
    }

    private static SheetData BuildSheetData(
        IReadOnlyList<object?[]> rows,
        List<string> sharedStrings,
        Dictionary<string, int> sharedStringIndexes)
    {
        var sheetData = new SheetData();

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new Row { RowIndex = (uint)(rowIndex + 1) };

            for (var columnIndex = 0; columnIndex < rows[rowIndex].Length; columnIndex++)
            {
                var value = rows[rowIndex][columnIndex];

                // A null is left out of the package entirely, which is how Excel itself stores a gap.
                if (value is null)
                {
                    continue;
                }

                row.Append(BuildCell(value, ColumnName(columnIndex) + (rowIndex + 1), sharedStrings, sharedStringIndexes));
            }

            sheetData.Append(row);
        }

        return sheetData;
    }

    private static Cell BuildCell(
        object value,
        string reference,
        List<string> sharedStrings,
        Dictionary<string, int> sharedStringIndexes)
    {
        var styleIndex = default(uint?);

        if (value is Styled styled)
        {
            styleIndex = styled.StyleIndex;
            value = styled.Value ?? string.Empty;
        }

        var cell = new Cell { CellReference = reference };

        switch (value)
        {
            case RawAttributes raw:
                // Attribute text is stored as written and parsed on first access, which is how a hostile
                // package throws long after the part opens.
                cell.CellReference = raw.Reference is null ? null : new StringValue { InnerText = raw.Reference };
                cell.StyleIndex = raw.StyleIndex is null ? null : new UInt32Value { InnerText = raw.StyleIndex };
                cell.DataType = raw.DataType is null ? null : new EnumValue<CellValues> { InnerText = raw.DataType };
                cell.CellValue = new CellValue(raw.Value);
                return cell;

            case RawSharedString shared:
                cell.DataType = CellValues.SharedString;
                cell.CellValue = new CellValue(shared.Index);
                break;

            case InlineText inline:
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(inline.Value));
                break;

            case CellError error:
                cell.DataType = CellValues.Error;
                cell.CellValue = new CellValue(error.Value);
                break;

            case Formula formula:
                cell.CellFormula = new CellFormula(formula.Expression);
                cell.CellValue = new CellValue(formula.CachedValue);
                if (formula.IsText)
                {
                    cell.DataType = CellValues.String;
                }

                break;

            case bool flag:
                cell.DataType = CellValues.Boolean;
                cell.CellValue = new CellValue(flag ? "1" : "0");
                break;

            case DateTime date:
                styleIndex ??= BuiltInDateStyle;
                cell.CellValue = new CellValue(date.ToOADate().ToString("R", CultureInfo.InvariantCulture));
                break;

            case double or int or long or decimal:
                cell.CellValue = new CellValue(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                break;

            default:
                cell.DataType = CellValues.SharedString;
                cell.CellValue = new CellValue(SharedStringIndex((string)value, sharedStrings, sharedStringIndexes)
                    .ToString(CultureInfo.InvariantCulture));
                break;
        }

        if (styleIndex is { } index)
        {
            cell.StyleIndex = index;
        }

        return cell;
    }

    private static int SharedStringIndex(string value, List<string> sharedStrings, Dictionary<string, int> indexes)
    {
        if (indexes.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var index = sharedStrings.Count;
        sharedStrings.Add(value);
        indexes[value] = index;

        return index;
    }

    private static void AddSharedStringTable(WorkbookPart workbookPart, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var part = workbookPart.AddNewPart<SharedStringTablePart>();
        var table = new SharedStringTable();

        foreach (var value in values)
        {
            table.Append(new SharedStringItem(new Text(value)));
        }

        part.SharedStringTable = table;
        part.SharedStringTable.Save();
    }

    private static void AddStylesheet(WorkbookPart workbookPart)
    {
        var part = workbookPart.AddNewPart<WorkbookStylesPart>();

        part.Stylesheet = new Stylesheet(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164U, FormatCode = "dd/mm/yyyy hh:mm" },
                new NumberingFormat { NumberFormatId = 165U, FormatCode = "#,##0.00" }),
            new Fonts(new Font()),
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None })),
            new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(
                new CellFormat { NumberFormatId = 0U },
                new CellFormat { NumberFormatId = 14U, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 164U, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 165U, ApplyNumberFormat = true }));

        part.Stylesheet.Save();
    }

    private static string ColumnName(int columnIndex)
    {
        var name = string.Empty;

        for (var remaining = columnIndex + 1; remaining > 0; remaining = (remaining - 1) / 26)
        {
            name = (char)('A' + ((remaining - 1) % 26)) + name;
        }

        return name;
    }
}
