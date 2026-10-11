using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Enterprise.Gpt.Service.Agents.Models;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf.IO;
using Paragraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using Sheet = DocumentFormat.OpenXml.Spreadsheet.Sheet;
using Table = DocumentFormat.OpenXml.Wordprocessing.Table;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>Re-opens a produced artifact and measures it, before it is stored.</summary>
/// <remarks>
/// Deterministic and free (no model, sandbox or network), and run against the exact bytes that will be stored.
/// </remarks>
public interface IGeneratedArtifactVerifier
{
    /// <summary>
    /// Opens an artifact with the library that owns its format (by file extension) and reports what it holds.
    /// </summary>
    /// <returns>The verdict. A file that cannot be opened is a failed check, never an exception.</returns>
    ArtifactVerification Verify(string fileName, byte[] content);
}

/// <inheritdoc />
public sealed partial class GeneratedArtifactVerifier(ILogger<GeneratedArtifactVerifier> logger) : IGeneratedArtifactVerifier
{
    // Permissive on purpose: a ragged row is a shape observation, not a parse failure, and refusing one
    // here would discard a spreadsheet the user can open perfectly well.
    private static readonly CsvConfiguration _csvConfiguration = new(CultureInfo.InvariantCulture)
    {
        DetectDelimiter = true,
        BadDataFound = null,
        MissingFieldFound = null
    };

    private readonly ILogger<GeneratedArtifactVerifier> _logger = logger;

    /// <inheritdoc />
    public ArtifactVerification Verify(string fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (content.Length == 0)
        {
            return ArtifactVerification.Fail("it is empty");
        }

        try
        {
            return extension switch
            {
                ".docx" => VerifyWord(content),
                ".xlsx" => VerifySpreadsheet(content),
                ".pptx" => VerifyPresentation(content),
                ".pdf" => VerifyPdf(content),
                ".csv" => VerifyCsv(content),
                ".md" or ".txt" => VerifyText(content),
                _ => ArtifactVerification.Fail("it is not a format this platform produces")
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Logged and never relayed: a parser's own message can quote the bytes it choked on, and
            // those bytes are the document's content.
            LogArtifactReopenFailed(_logger, exception, extension);

            return ArtifactVerification.Fail($"it could not be re-opened as {extension.TrimStart('.')}");
        }
    }

    #region Private methods

    private static ArtifactVerification VerifyWord(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);

        if (document.MainDocumentPart is not { } main)
        {
            return ArtifactVerification.Fail("the Word document carries no body");
        }

        // Streamed rather than walked as a DOM: a stored artifact is bounded only by the 50 MB upload
        // ceiling, and counting two element types does not need the whole part in memory.
        int paragraphs = 0, tables = 0;
        using var reader = OpenXmlReader.Create(main);

        while (reader.Read())
        {
            if (!reader.IsStartElement)
            {
                continue;
            }

            if (reader.ElementType == typeof(Paragraph))
            {
                paragraphs++;
            }
            else if (reader.ElementType == typeof(Table))
            {
                tables++;
            }
        }

        return paragraphs + tables == 0
            ? ArtifactVerification.Fail("the Word document is empty")
            : ArtifactVerification.Pass($"{Count(paragraphs, "paragraph")}, {Count(tables, "table")}");
    }

    private static ArtifactVerification VerifySpreadsheet(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);

        List<string> names =
        [
            .. (document.WorkbookPart?.Workbook?.Sheets?.Elements<Sheet>() ?? [])
                .Select(sheet => sheet.Name?.Value)
                .OfType<string>()
        ];

        return names.Count == 0
            ? ArtifactVerification.Fail("the workbook has no sheets")
            : ArtifactVerification.Pass($"{Count(names.Count, "sheet")} ({string.Join(", ", names)})");
    }

    private static ArtifactVerification VerifyPresentation(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var document = PresentationDocument.Open(stream, isEditable: false);

        var slides = document.PresentationPart?.SlideParts.Count() ?? 0;

        return slides == 0
            ? ArtifactVerification.Fail("the deck has no slides")
            : ArtifactVerification.Pass(Count(slides, "slide"));
    }

    private static ArtifactVerification VerifyPdf(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);

        // Import, not Modify, so no font resolves: only the export renderer registers a font resolver.
        // InformationOnly is not implemented in PDFsharp 6.
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

        return document.PageCount == 0
            ? ArtifactVerification.Fail("the PDF has no pages")
            : ArtifactVerification.Pass(Count(document.PageCount, "page"));
    }

    private static ArtifactVerification VerifyCsv(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var parser = new CsvParser(reader, _csvConfiguration);

        if (!parser.Read() || parser.Count == 0 || Array.TrueForAll(parser.Record ?? [], string.IsNullOrWhiteSpace))
        {
            return ArtifactVerification.Fail("it has no header row");
        }

        var columns = parser.Count;
        var rows = 0;

        while (parser.Read())
        {
            rows++;
        }

        return ArtifactVerification.Pass($"{Count(columns, "column")}, {Count(rows, "data row")}");
    }

    private static ArtifactVerification VerifyText(byte[] content)
    {
        // A throwing decoder rather than the replacement default: a file this platform wrote and cannot
        // read back as UTF-8 is one the user's editor will open as mojibake.
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(content);

        return string.IsNullOrWhiteSpace(text)
            ? ArtifactVerification.Fail("it holds no text")
            : ArtifactVerification.Pass(Count(text.Length, "character"));
    }

    private static string Count(int value, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{value} {noun}{(value == 1 ? string.Empty : "s")}");

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "A generated {Extension} artifact could not be re-opened")]
    private static partial void LogArtifactReopenFailed(ILogger logger, Exception exception, string extension);

    #endregion
}
