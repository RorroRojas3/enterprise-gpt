using FluentValidation;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;

namespace Enterprise.Gpt.Service.Validators
{
    /// <summary>
    /// Validates an uploaded file before it is queued for ingestion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This validator lives in the service project rather than beside the DTO — the repository's usual home
    /// for validators — because its rules depend on services the DTO project cannot reference: the size
    /// limit comes from <see cref="DocumentOptions"/> and the accepted format list from the registered
    /// extractors.
    /// </para>
    /// <para>
    /// Running these checks up front matters: without them a bad upload is accepted with a 202 and only
    /// reveals itself as a failed job minutes later, which is a poor experience and burns OCR spend.
    /// </para>
    /// </remarks>
    public sealed class UploadedFileValidator : AbstractValidator<FileDto>
    {
        // How much of a text file to scan for the NUL bytes that mark it as binary. Scanning the head is
        // enough to catch a renamed executable or image without walking a large file twice.
        private const int TextProbeLength = 8192;

        private static readonly byte[] _pdfSignature = "%PDF"u8.ToArray();
        private static readonly byte[] _zipSignature = [0x50, 0x4B];                                     // PK — OOXML (.docx, .pptx)
        private static readonly byte[] _ole2Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]; // legacy Office (.doc)

        // The UTF-16 LE mark is also the prefix of the UTF-32 LE mark, so three cover all four encodings.
        private static readonly byte[] _utf16LittleEndianBom = [0xFF, 0xFE];
        private static readonly byte[] _utf16BigEndianBom = [0xFE, 0xFF];
        private static readonly byte[] _utf32BigEndianBom = [0x00, 0x00, 0xFE, 0xFF];

        /// <summary>
        /// Initializes a new instance of the <see cref="UploadedFileValidator"/> class.
        /// </summary>
        /// <param name="options">Supplies the maximum accepted upload size.</param>
        /// <param name="extractorFactory">Supplies the set of formats that can actually be read.</param>
        public UploadedFileValidator(IOptions<DocumentOptions> options, IDocumentTextExtractorFactory extractorFactory)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(extractorFactory);

            var maxSizeBytes = options.Value.MaxFileSizeBytes;
            var supportedNames = string.Join(", ", extractorFactory.SupportedExtensionNames);

            RuleFor(x => x.FileName)
                .NotEmpty()
                .WithMessage("A file name is required.")
                .MaximumLength(256)
                .WithMessage("The file name must be 256 characters or fewer.");

            RuleFor(x => x.Content)
                .NotEmpty()
                .WithMessage("The file is empty.");

            RuleFor(x => x.Content.LongLength)
                .LessThanOrEqualTo(maxSizeBytes)
                // Without the name, the expression path becomes the wire-visible key in the problem
                // details errors dictionary — "Content.LongLength" rather than the DTO's property.
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

        /// <summary>
        /// Confirms the leading bytes match the format the extension claims.
        /// </summary>
        /// <remarks>
        /// Extensions are attacker-controlled, and every downstream reader assumes a particular container.
        /// Handing a renamed file to the Open XML or OLE2 reader turns a user mistake into a parser fault,
        /// and handing one to Document Intelligence spends money to learn the same thing.
        /// </remarks>
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
                FileExtensions.Docx or FileExtensions.Pptx => StartsWith(content, _zipSignature),
                FileExtensions.Doc => StartsWith(content, _ole2Signature),
                // Text formats have no signature; rejecting embedded NUL bytes is what separates real text
                // from a binary file wearing a .txt extension.
                FileExtensions.Md or FileExtensions.Txt => !LooksBinary(content),
                _ => true,
            };
        }

        private static bool StartsWith(byte[] content, byte[] signature)
        {
            return content.Length >= signature.Length
                && content.AsSpan(0, signature.Length).SequenceEqual(signature);
        }

        /// <summary>
        /// Heuristically decides whether a file claiming to be text is really binary.
        /// </summary>
        /// <remarks>
        /// A UTF-16 or UTF-32 file carries a NUL in most of its bytes, so the NUL probe alone would reject
        /// every "Unicode"-encoded text file Notepad produces — exactly the files
        /// <see cref="Extraction.PlainTextExtractor"/> is built to decode. A byte-order mark identifies
        /// those encodings, and the extractor honours the same marks, so a marked file skips the probe.
        /// Prepending a mark to genuinely binary content is not a hole worth closing: unlike the container
        /// formats, the text path feeds a <see cref="StreamReader"/> rather than a parser, so the worst case
        /// is a document of garbage text, which then fails as having no readable content.
        /// </remarks>
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
                // FileDto throws for an extension it does not know at all; that is a validation failure
                // here, not an exceptional condition.
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
    }
}
