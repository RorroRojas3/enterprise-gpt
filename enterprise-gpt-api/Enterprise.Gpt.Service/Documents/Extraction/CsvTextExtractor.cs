using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Documents.Chunking;
using Enterprise.Gpt.Service.Documents.Extraction.Interfaces;
using Enterprise.Gpt.Service.Documents.Extraction.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// CsvHelper has its own ValidationException; extractors raise FluentValidation's.
using ValidationException = FluentValidation.ValidationException;

namespace Enterprise.Gpt.Service.Documents.Extraction;

/// <summary>
/// Reads delimited text (<c>.csv</c>) files locally, as a single-sheet workbook.
/// </summary>
/// <remarks>
/// Read with <see cref="CsvParser"/>, not a record reader, so <see cref="SheetAssembler"/> picks the header
/// on the same terms as a worksheet's.
/// </remarks>
public sealed partial class CsvTextExtractor(
    ILogger<CsvTextExtractor> logger,
    IOptions<SheetOptions> options,
    ITextChunker textChunker) : ISheetStructureExtractor
{
    private const char Quote = '"';

    private static readonly char[] _candidateDelimiters = [',', ';', '\t'];

    private const int DelimiterSampleLength = 64 * 1024;

    // Stored cells repeat their column name plus JSON punctuation; 8x the text budget fits a wide export
    // but refuses a sheet whose headings outweigh its data.
    private const int CellCharactersPerTextCharacter = 8;

    private readonly ILogger<CsvTextExtractor> _logger = logger;
    private readonly SheetOptions _options = options.Value;
    private readonly ITextChunker _textChunker = textChunker;

    /// <inheritdoc />
    public IReadOnlyCollection<FileExtensions> SupportedExtensions { get; } = [FileExtensions.Csv];

    /// <inheritdoc />
    public Task<SheetExtractionResult> ExtractSheetsAsync(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        return Task.FromResult(Read(file, progress, cancellationToken));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        return Task.FromResult(Read(file, progress, cancellationToken).Segments);
    }

    #region Private methods

    private SheetExtractionResult Read(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return ExtractRows(file, progress, cancellationToken);
        }
        catch (CsvHelperException ex)
        {
            LogCsvReadFailed(_logger, ex);
            throw new ValidationException(
            [
                new FluentValidation.Results.ValidationFailure(nameof(FileDto.FileName),
                    "The file could not be read as CSV. Its quoting or row structure may be malformed.")
            ]);
        }
    }

    private SheetExtractionResult ExtractRows(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sheetName = ResolveSheetName(file.FileName);

        progress?.Report(new DocumentExtractionProgress(null, "Reading the spreadsheet"));

        var text = DecodeText(file.Content);
        var delimiter = ResolveDelimiter(text);

        // Before parsing: CsvParser buffers a whole record before its field count is readable.
        GuardShape(text, delimiter, sheetName, cancellationToken);

        using var reader = new StringReader(text);
        using var parser = new CsvParser(reader, BuildConfiguration(delimiter));

        List<string[]> rows = [];
        List<string> cells = [];
        var characters = SheetSegmentBuilder.SheetHeaderLength(sheetName);

        while (parser.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (parser.Count > _options.MaxColumnsPerSheet)
            {
                throw SheetSegmentBuilder.ColumnLimitExceeded(sheetName, _options.MaxColumnsPerSheet);
            }

            cells.Clear();

            for (var i = 0; i < parser.Count; i++)
            {
                cells.Add(SheetSegmentBuilder.NormalizeCell(parser[i]));
            }

            var lastIndex = SheetSegmentBuilder.LastPopulatedIndex(cells);
            if (lastIndex < 0)
            {
                continue;
            }

            // The only row ceiling a CSV reaches: one sheet, and the upload ceiling is never below this one.
            if (rows.Count == _options.MaxRowsPerSheet)
            {
                throw SheetSegmentBuilder.RowLimitExceeded(sheetName, _options.MaxRowsPerSheet);
            }

            var row = new string[lastIndex + 1];
            cells.CopyTo(0, row, 0, row.Length);
            rows.Add(row);

            characters += SheetSegmentBuilder.RowLength(row, lastIndex);

            if (characters > _options.MaxCharactersPerUpload)
            {
                throw SheetSegmentBuilder.TextLimitExceeded(_options.MaxCharactersPerUpload);
            }
        }

        progress?.Report(new DocumentExtractionProgress(1d, $"Read {rows.Count} row(s)"));
        LogRowsExtracted(_logger, rows.Count);

        if (rows.Count == 0)
        {
            return new SheetExtractionResult([], []);
        }

        var assembly = SheetAssembler.Assemble(
            sheetName, 1, rows, _options.RowWindow, _textChunker,
            CellBudget(), cancellationToken);

        return new SheetExtractionResult(assembly.Segments, [assembly.Structure]);
    }

    private int CellBudget()
    {
        return (int)Math.Min(int.MaxValue, (long)_options.MaxCharactersPerUpload * CellCharactersPerTextCharacter);
    }

    private static string DecodeText(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);

        // UTF-8 is only the fallback; a UTF-16/32 BOM (as Windows editors emit) wins.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    // BadDataFound cleared so a stray quote (an inch mark, a quoted nickname) is read verbatim rather than
    // refusing the whole file.
    private static CsvConfiguration BuildConfiguration(char delimiter)
    {
        return new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter.ToString(),
            BadDataFound = null
        };
    }

    // Not the library's detector, which prefers the culture's list separator: a semicolon export with
    // decimal commas on every line would silently split on commas.
    private static char ResolveDelimiter(string text)
    {
        var lineHits = new int[_candidateDelimiters.Length];
        var totals = new int[_candidateDelimiters.Length];
        var counts = new int[_candidateDelimiters.Length];
        var inQuotes = false;

        var length = Math.Min(text.Length, DelimiterSampleLength);

        for (var i = 0; i < length; i++)
        {
            var c = text[i];

            if (c == Quote)
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && c is '\r' or '\n')
            {
                Accumulate(counts, lineHits, totals);
                continue;
            }

            if (inQuotes)
            {
                continue;
            }

            var candidate = Array.IndexOf(_candidateDelimiters, c);
            if (candidate >= 0)
            {
                counts[candidate]++;
            }
        }

        // The last line counts only if the file, not the sample window, ended there.
        if (length == text.Length)
        {
            Accumulate(counts, lineHits, totals);
        }

        var best = 0;

        for (var i = 1; i < _candidateDelimiters.Length; i++)
        {
            if (lineHits[i] > lineHits[best] || (lineHits[i] == lineHits[best] && totals[i] > totals[best]))
            {
                best = i;
            }
        }

        return lineHits[best] == 0 ? _candidateDelimiters[0] : _candidateDelimiters[best];
    }

    private static void Accumulate(int[] counts, int[] lineHits, int[] totals)
    {
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                lineHits[i]++;
                totals[i] += counts[i];
                counts[i] = 0;
            }
        }
    }

    private void GuardShape(string text, char delimiter, string sheetName, CancellationToken cancellationToken)
    {
        var fields = 1;
        var rows = 0;
        var populated = false;
        var inQuotes = false;
        var atFieldStart = true;

        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var c = text[i];

            if (inQuotes)
            {
                if (c != Quote)
                {
                    populated |= !char.IsWhiteSpace(c);
                    continue;
                }

                // A doubled quote is an escaped one and does not close the field.
                if (i + 1 < text.Length && text[i + 1] == Quote)
                {
                    i++;
                    populated = true;
                    continue;
                }

                inQuotes = false;
                continue;
            }

            if (c == Quote && atFieldStart)
            {
                inQuotes = true;
                atFieldStart = false;
                continue;
            }

            if (c == delimiter)
            {
                if (++fields > _options.MaxColumnsPerSheet)
                {
                    throw SheetSegmentBuilder.ColumnLimitExceeded(sheetName, _options.MaxColumnsPerSheet);
                }

                atFieldStart = true;
                continue;
            }

            if (c is '\r' or '\n')
            {
                if (populated && ++rows > _options.MaxRowsPerSheet)
                {
                    throw SheetSegmentBuilder.RowLimitExceeded(sheetName, _options.MaxRowsPerSheet);
                }

                fields = 1;
                populated = false;
                atFieldStart = true;
                continue;
            }

            atFieldStart = false;
            populated |= !char.IsWhiteSpace(c);
        }

        if (populated && ++rows > _options.MaxRowsPerSheet)
        {
            throw SheetSegmentBuilder.RowLimitExceeded(sheetName, _options.MaxRowsPerSheet);
        }
    }

    private static string ResolveSheetName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        return string.IsNullOrWhiteSpace(name)
            ? "Sheet1"
            : SheetSegmentBuilder.TruncateName(SheetSegmentBuilder.NormalizeCell(name));
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read a CSV file")]
    private static partial void LogCsvReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracted {RowCount} row(s) from a CSV file")]
    private static partial void LogRowsExtracted(ILogger logger, int rowCount);

    #endregion
}
