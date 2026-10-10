using System.IO.Compression;
using System.Text;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// Opens a converted artifact on the API host and decides whether it is the format it claims to be.
/// </summary>
/// <remarks>
/// Host-side on purpose: the sandbox reporting success means the Python did not raise, not that a
/// readable file arrived. No document library — Office formats are OPC packages whose defining part a
/// zip reader can find, and a PDF announces itself in its first five bytes, so a parser would only add
/// a second thing that can be wrong.
/// </remarks>
internal static class ArtifactVerifier
{
    private const int MinimumPlausibleBytes = 32;

    /// <summary>Verifies an artifact against the format it was converted to.</summary>
    /// <param name="format">The target format's extension, without a dot.</param>
    /// <returns>Whether it verified, and what was found either way.</returns>
    public static (bool Verified, string Detail) Verify(string format, byte[] bytes)
    {
        if (bytes.Length < MinimumPlausibleBytes)
        {
            return (false, $"Only {bytes.Length} bytes came back, which is too few to be a {format} file.");
        }

        try
        {
            return format switch
            {
                "docx" => Package(bytes, "word/document.xml", format),
                "xlsx" => Package(bytes, "xl/workbook.xml", format),
                "pptx" => Package(bytes, "ppt/presentation.xml", format),
                "pdf" => Pdf(bytes),
                "csv" => Csv(bytes),
                "md" or "txt" => Text(bytes, format),
                _ => (false, $"No verifier is defined for '{format}'."),
            };
        }
        catch (Exception exception)
        {
            return (false, $"{exception.GetType().Name} while reading the {format}: {exception.Message}");
        }
    }

    private static (bool, string) Package(byte[] bytes, string requiredPart, string format)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var part = archive.GetEntry(requiredPart);

        return part is null
            ? (false, $"The {format} opened as a package but carries no {requiredPart}, so no reader will accept it. "
                + $"Parts present: {string.Join(", ", archive.Entries.Take(8).Select(entry => entry.FullName))}.")
            : (true, $"Opened as an OPC package of {archive.Entries.Count} parts, with {requiredPart} at "
                + $"{part.Length} bytes.");
    }

    private static (bool, string) Pdf(byte[] bytes)
    {
        var header = Encoding.ASCII.GetString(bytes, 0, 5);

        if (header != "%PDF-")
        {
            return (false, $"The first five bytes are '{header}', not '%PDF-'.");
        }

        var text = Encoding.Latin1.GetString(bytes);

        if (!text.Contains("%%EOF", StringComparison.Ordinal))
        {
            return (false, "The file carries no %%EOF marker, so it was truncated before it was finished.");
        }

        // Counting page objects rather than parsing the page tree: enough to tell a one-page render
        // from a whole document, and it cannot itself throw on a structure a parser would reject.
        var pages = text.Split("/Type", StringSplitOptions.None)
            .Count(fragment => fragment.TrimStart().StartsWith("/Page ", StringComparison.Ordinal)
                || fragment.TrimStart().StartsWith("/Page\n", StringComparison.Ordinal)
                || fragment.TrimStart().StartsWith("/Page/", StringComparison.Ordinal)
                || fragment.TrimStart().StartsWith("/Page>", StringComparison.Ordinal));

        return (true, $"A complete PDF of {bytes.Length} bytes carrying roughly {pages} page object(s).");
    }

    private static (bool, string) Csv(byte[] bytes)
    {
        var lines = Decode(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count < 2)
        {
            return (false, $"Only {lines.Count} non-empty line(s), so there is no header and a row to check against it.");
        }

        var separators = lines[0].Count(character => character == ',');
        var mismatched = lines.Skip(1).Count(line => line.Count(character => character == ',') != separators);

        return mismatched > 0
            ? (false, $"{mismatched} of {lines.Count - 1} rows carry a different field count than the header.")
            : (true, $"{lines.Count - 1} rows over a header of {separators + 1} fields.");
    }

    private static (bool, string) Text(byte[] bytes, string format)
    {
        var text = Decode(bytes);

        return text.Trim().Length == 0
            ? (false, $"The {format} decoded to nothing but whitespace.")
            : (true, $"{text.Length} characters over {text.Split('\n').Length} line(s).");
    }

    // Strict, so bytes that are not actually text fail here rather than arriving as replacement
    // characters and passing a length check.
    private static string Decode(byte[] bytes) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
}
