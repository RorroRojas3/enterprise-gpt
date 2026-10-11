using Enterprise.Gpt.Common.Constants.Documents;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Documents.Extraction;
using Enterprise.Gpt.Service.Options;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents;

/// <summary>
/// Validates an uploaded file before it is queued for ingestion.
/// </summary>
/// <remarks>
/// Lives in the service project, not beside the DTO, because it needs <see cref="DocumentOptions"/> and the
/// registered extractors. Checking up front avoids a 202 that fails minutes later and burns OCR spend.
/// </remarks>
public sealed class FileDtoValidator : AbstractValidator<FileDto>
{
    // Head of a text file probed for NUL bytes; enough to catch a renamed binary.
    private const int TextProbeLength = 8192;

    private static readonly byte[] _pdfSignature = "%PDF"u8.ToArray();
    private static readonly byte[] _zipSignature = [0x50, 0x4B];                                     // PK — OOXML (.docx, .pptx, .xlsx)
    private static readonly byte[] _ole2Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]; // legacy Office (.doc)

    // The UTF-16 LE mark is also the prefix of the UTF-32 LE mark, so three cover all four encodings.
    private static readonly byte[] _utf16LittleEndianBom = [0xFF, 0xFE];
    private static readonly byte[] _utf16BigEndianBom = [0xFE, 0xFF];
    private static readonly byte[] _utf32BigEndianBom = [0x00, 0x00, 0xFE, 0xFF];

    public FileDtoValidator(IOptions<DocumentOptions> options, IDocumentTextExtractorFactory extractorFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(extractorFactory);

        var maxSizeBytes = options.Value.MaxFileSizeBytes;
        var supportedNames = string.Join(", ", extractorFactory.SupportedExtensionNames);

        RuleFor(x => x.FileName)
            .NotEmpty()
            .WithMessage("A file name is required.")
            .MaximumLength(DocumentLimits.NameMaxLength)
            .WithMessage("The file name must be 256 characters or fewer.");

        RuleFor(x => x.Content)
            .NotEmpty()
            .WithMessage("The file is empty.");

        RuleFor(x => x.Content.LongLength)
            .LessThanOrEqualTo(maxSizeBytes)
            // Otherwise the wire-visible errors key would be "Content.LongLength".
            .WithName(nameof(FileDto.Content))
            .WithMessage($"The file exceeds the maximum upload size of {FormatSize(maxSizeBytes)}.")
            .When(x => x.Content is { Length: > 0 });

        RuleFor(x => x)
            .Must(file => TryGetExtension(file, out var extension) && extractorFactory.IsSupported(extension))
            .WithName(nameof(FileDto.FileName))
            .WithMessage(file => $"Files of type '{GetExtensionText(file)}' are not supported. Supported types: {supportedNames}.")
            .When(x => !string.IsNullOrWhiteSpace(x.FileName));

        RuleFor(x => x)
            .Must(HasContentMatchingExtension)
            .WithName(nameof(FileDto.Content))
            .WithMessage("The file contents do not match its extension. Rename it to its real format and try again.")
            .When(x => x.Content is { Length: > 0 }
                && TryGetExtension(x, out var extension)
                && extractorFactory.IsSupported(extension));
    }

    #region Private methods

    // Extensions are attacker-controlled; a renamed file would fault the Open XML or OLE2 readers or
    // waste a Document Intelligence call.
    private static bool HasContentMatchingExtension(FileDto file)
    {
        if (!TryGetExtension(file, out var extension))
        {
            return false;
        }

        var content = file.Content;

        return extension switch
        {
            FileExtensions.Pdf => StartsWith(content, _pdfSignature),
            FileExtensions.Docx or FileExtensions.Pptx or FileExtensions.Xlsx => StartsWith(content, _zipSignature),
            FileExtensions.Doc => StartsWith(content, _ole2Signature),
            // Text has no signature; embedded NULs mark a binary file wearing a text extension.
            FileExtensions.Csv or FileExtensions.Md or FileExtensions.Txt => !LooksBinary(content),
            _ => true,
        };
    }

    private static bool StartsWith(byte[] content, byte[] signature)
    {
        return content.Length >= signature.Length
            && content.AsSpan(0, signature.Length).SequenceEqual(signature);
    }

    // A BOM skips the NUL probe: UTF-16/32 text is mostly NULs, and PlainTextExtractor decodes it. A BOM
    // on binary content only yields garbage text, which then fails as unreadable.
    private static bool LooksBinary(byte[] content)
    {
        if (HasUnicodeByteOrderMark(content))
        {
            return false;
        }

        return content.AsSpan(0, Math.Min(TextProbeLength, content.Length)).Contains((byte)0);
    }

    private static bool HasUnicodeByteOrderMark(byte[] content)
    {
        return StartsWith(content, _utf16LittleEndianBom)
            || StartsWith(content, _utf16BigEndianBom)
            || StartsWith(content, _utf32BigEndianBom);
    }

    private static bool TryGetExtension(FileDto file, out FileExtensions extension)
    {
        try
        {
            extension = file.FileExtension;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            // FileDto throws for an unknown extension; here that is a validation failure.
            extension = default;
            return false;
        }
    }

    private static string GetExtensionText(FileDto file)
    {
        var extension = Path.GetExtension(file.FileName);
        return string.IsNullOrEmpty(extension) ? "(none)" : extension.ToLowerInvariant();
    }

    private static string FormatSize(long bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / (1024 * 1024)} MB"
            : $"{bytes / 1024} KB";
    }

    #endregion
}
