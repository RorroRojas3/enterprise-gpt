using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Settings;
using System.Globalization;
using System.Text;
// CsvHelper ships a ValidationException of its own; the 400 this extractor raises is the
// FluentValidation one every other extractor raises.
using ValidationException = FluentValidation.ValidationException;

namespace Enterprise.Gpt.Service.Extraction;

/// <summary>
/// Reads delimited text (<c>.csv</c>) files locally, as a single-sheet workbook.
/// </summary>
/// <remarks>
/// Rows are read through <see cref="CsvParser"/> rather than a record reader, so every line reaches
/// the grid intact and which one is a header is decided once, by
/// <see cref="SheetAssembler"/>, on the same terms as a worksheet's.
/// </remarks>
public sealed class CsvTextExtractor(
    ILogger<CsvTextExtractor> logger,
    IOptions<SheetOptions> options,
    ITextChunker textChunker) : ISheetStructureExtractor
{
    private const char Quote = '"';

    private static readonly char[] _candidateDelimiters = [',', ';', '\t'];

    // How much of the file the delimiter is resolved from. Enough lines to rank candidates without
    // walking a large file twice.
    private const int DelimiterSampleLength = 64 * 1024;

    // What the same cells cost once stored: every populated one repeats its column's name, plus JSON
    // punctuation. Eight times the text budget leaves an ordinary wide export room and still refuses a
    // sheet whose headings alone would outweigh its data.
    private const int CellCharactersPerTextCharacter = 8;

    private readonly ILogger<CsvTextExtractor> _logger = logger;
    private readonly SheetOptions _options = options.Value;
    private readonly ITextChunker _textChunker = textChunker;

    /// <inheritdoc />
    public IReadOnlyCollection<FileExtensions> SupportedExtensions { get; } = [FileExtensions.Csv];

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        return Task.FromResult(Read(file, progress, cancellationToken).Segments);
    }

    /// <inheritdoc />
    public Task<SheetExtractionResult> ExtractSheetsAsync(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        return Task.FromResult(Read(file, progress, cancellationToken));
    }

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
            _logger.LogWarning(ex, "Failed to read a CSV file");
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

        // The shape is checked before the parser runs because CsvParser buffers a whole record before its
        // field count can be read, so one pathological line would allocate for every field in it first.
        GuardShape(text, delimiter, sheetName, cancellationToken);

        using var reader = new StringReader(text);
        using var parser = new CsvParser(reader, BuildConfiguration(delimiter));

        var rows = new List<string[]>();
        var cells = new List<string>();
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

            // The whole file is one sheet, and the upload ceiling is never below the per-sheet one,
            // so this is the only row ceiling a CSV can reach.
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
        _logger.LogInformation("Extracted {RowCount} row(s) from a CSV file", rows.Count);

        if (rows.Count == 0)
        {
            return new SheetExtractionResult([], []);
        }

        var assembly = SheetAssembler.Assemble(
            sheetName, 1, rows, _options.RowWindow, _textChunker,
            CellBudget(), cancellationToken);

        return new SheetExtractionResult(assembly.Segments, [assembly.Structure]);
    }

    /// <summary>
    /// Serialized cell characters one upload may materialize, derived from its text budget.
    /// </summary>
    private int CellBudget()
    {
        return (int)Math.Min(int.MaxValue, (long)_options.MaxCharactersPerUpload * CellCharactersPerTextCharacter);
    }

    private static string DecodeText(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);

        // UTF-8 is the fallback rather than the assumption: a byte-order mark for UTF-16/UTF-32 wins,
        // which is what Notepad and most Windows editors emit.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    /// <remarks>
    /// <c>BadDataFound</c> is cleared so a stray quote in an unquoted field keeps its raw text instead of
    /// refusing the upload: an inch mark or a nickname in quotes is ordinary in an exported CSV, and
    /// losing a whole file to one is worse than reading that cell verbatim.
    /// </remarks>
    private static CsvConfiguration BuildConfiguration(char delimiter)
    {
        return new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter.ToString(),
            BadDataFound = null
        };
    }

    /// <summary>
    /// Picks the candidate delimiter present on the most lines of the file's opening sample.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than through the library's own detector, which prefers the culture's list
    /// separator over the strongest candidate: a semicolon-delimited export whose decimal commas appear
    /// on every line would resolve to a comma and split silently into the wrong columns.
    /// </remarks>
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

        // The sample is cut at an arbitrary offset, so its last line is only ranked when the file ended
        // there rather than when the window did.
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

    /// <summary>
    /// Refuses a file whose records exceed a ceiling, before any of it is buffered.
    /// </summary>
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
}
