using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Common.Extensions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Service.Extraction
{
    /// <summary>
    /// Selects the <see cref="IDocumentTextExtractor"/> that can read a given file extension, and is the
    /// single source of truth for which extensions the API accepts.
    /// </summary>
    public interface IDocumentTextExtractorFactory
    {
        /// <summary>
        /// Every extension the registered extractors can read, ordered for stable presentation.
        /// </summary>
        IReadOnlyList<FileExtensions> SupportedExtensions { get; }

        /// <summary>
        /// Every supported extension rendered as its dotted form (for example <c>.pdf</c>).
        /// </summary>
        IReadOnlyList<string> SupportedExtensionNames { get; }

        /// <summary>
        /// Indicates whether any registered extractor can read <paramref name="extension"/>.
        /// </summary>
        /// <param name="extension">The extension to test.</param>
        /// <returns><see langword="true"/> when the extension is supported; otherwise <see langword="false"/>.</returns>
        bool IsSupported(FileExtensions extension);

        /// <summary>
        /// Returns the extractor for <paramref name="extension"/>.
        /// </summary>
        /// <param name="extension">The extension to resolve.</param>
        /// <returns>The matching <see cref="IDocumentTextExtractor"/>.</returns>
        /// <exception cref="ValidationException">Thrown when no registered extractor supports the extension.</exception>
        IDocumentTextExtractor Resolve(FileExtensions extension);
    }

    /// <inheritdoc cref="IDocumentTextExtractorFactory" />
    public sealed class DocumentTextExtractorFactory : IDocumentTextExtractorFactory
    {
        private readonly Dictionary<FileExtensions, IDocumentTextExtractor> _byExtension;

        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentTextExtractorFactory"/> class.
        /// </summary>
        /// <param name="extractors">Every registered extractor.</param>
        /// <param name="logger">Logger used to record the resolved format map at startup.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when two extractors claim the same extension. This is a wiring mistake rather than a
        /// runtime condition, so it fails fast at construction instead of silently picking a winner.
        /// </exception>
        public DocumentTextExtractorFactory(IEnumerable<IDocumentTextExtractor> extractors, ILogger<DocumentTextExtractorFactory> logger)
        {
            ArgumentNullException.ThrowIfNull(extractors);

            _byExtension = [];

            foreach (var extractor in extractors)
            {
                foreach (var extension in extractor.SupportedExtensions)
                {
                    if (_byExtension.TryGetValue(extension, out var existing))
                    {
                        throw new InvalidOperationException(
                            $"Both '{existing.GetType().Name}' and '{extractor.GetType().Name}' claim to handle '{extension.GetDescription()}'.");
                    }

                    _byExtension[extension] = extractor;
                }
            }

            SupportedExtensions = [.. _byExtension.Keys.OrderBy(x => x)];
            SupportedExtensionNames = [.. SupportedExtensions.Select(x => x.GetDescription())];

            logger.LogInformation("Document extraction supports {Count} format(s): {Extensions}",
                SupportedExtensionNames.Count, string.Join(", ", SupportedExtensionNames));
        }

        /// <inheritdoc />
        public IReadOnlyList<FileExtensions> SupportedExtensions { get; }

        /// <inheritdoc />
        public IReadOnlyList<string> SupportedExtensionNames { get; }

        /// <inheritdoc />
        public bool IsSupported(FileExtensions extension)
        {
            return _byExtension.ContainsKey(extension);
        }

        /// <inheritdoc />
        public IDocumentTextExtractor Resolve(FileExtensions extension)
        {
            if (_byExtension.TryGetValue(extension, out var extractor))
            {
                return extractor;
            }

            throw new ValidationException(
            [
                new ValidationFailure(nameof(FileDto.FileName),
                    $"Files of type '{extension.GetDescription()}' are not supported. Supported types: {string.Join(", ", SupportedExtensionNames)}.")
            ]);
        }
    }
}
