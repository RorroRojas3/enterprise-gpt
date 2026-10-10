using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Globalization;
using System.Text;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Builds in-memory <c>.xlsx</c> and <c>.csv</c> payloads so upload tests can exercise the spreadsheet
/// path without a committed binary fixture.
/// </summary>
public static class SpreadsheetFixture
{
    private static readonly string[] _regions = ["East", "West", "North", "South"];

    /// <summary>
    /// Creates a workbook with the given number of sheets, each carrying enough rows to produce several
    /// chunks while staying well under the test host's upload ceiling.
    /// </summary>
    public static byte[] CreateWorkbook(int sheetCount, int rowsPerSheet = 40)
    {
        using var stream = new MemoryStream();

        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var sheets = new Sheets();

            for (var i = 1; i <= sheetCount; i++)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                worksheetPart.Worksheet = new Worksheet(BuildSheetData(i, rowsPerSheet));

                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = (uint)i,
                    Name = $"Region {i}"
                });
            }

            workbookPart.Workbook.Append(sheets);
            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates a comma-delimited file with a header row and the given number of data rows.
    /// </summary>
    public static byte[] CreateCsv(int rowCount = 60)
    {
        var builder = new StringBuilder("SKU,Region,Revenue,Quarter\n");

        for (var i = 1; i <= rowCount; i++)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"SKU-{i:0000},{_regions[i % _regions.Length]},{i * 137},Q{(i % 4) + 1}\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static SheetData BuildSheetData(int sheetNumber, int rowCount)
    {
        var sheetData = new SheetData();
        sheetData.Append(BuildRow(1, "SKU", "Region", "Revenue", "Quarter"));

        for (var i = 1; i <= rowCount; i++)
        {
            sheetData.Append(BuildRow(
                (uint)(i + 1),
                $"SKU-{sheetNumber}-{i:0000}",
                _regions[i % _regions.Length],
                (i * 137).ToString(CultureInfo.InvariantCulture),
                $"Q{(i % 4) + 1}"));
        }

        return sheetData;
    }

    private static Row BuildRow(uint rowIndex, params string[] values)
    {
        var row = new Row { RowIndex = rowIndex };

        for (var i = 0; i < values.Length; i++)
        {
            // Inline strings keep the fixture to one part per sheet, which is all these tests need.
            row.Append(new Cell
            {
                CellReference = (char)('A' + i) + rowIndex.ToString(CultureInfo.InvariantCulture),
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[i]))
            });
        }

        return row;
    }
}
