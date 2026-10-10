using Enterprise.Gpt.Service.Exports;
using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

/// <summary>
/// Conversation export configuration, bound from the <c>Export</c> section and validated at startup.
/// </summary>
public sealed class ExportOptions
{
    /// <summary>
    /// Configuration section this type binds from.
    /// </summary>
    public const string SectionName = "Export";

    /// <summary>
    /// Export formats this deployment refuses, as their wire tokens (<c>docx</c>, <c>pdf</c>, …).
    /// Empty by default.
    /// </summary>
    /// <remarks>
    /// A disabled format's renderer is never registered, so the route answers it with the
    /// <c>export-renderer-not-configured</c> problem rather than with a rendered document. This is
    /// what makes that problem a real state rather than dead code — the renderers are otherwise
    /// in-process and always present, and the only other way to reach it is a PDF deployment with
    /// no font.
    /// </remarks>
    public string[] DisabledFormats { get; set; } = [];

    /// <summary>
    /// PDF rendering configuration.
    /// </summary>
    public PdfExportOptions Pdf { get; set; } = new();
}

internal sealed class ExportOptionsValidator : AbstractValidator<ExportOptions>
{
    public ExportOptionsValidator()
    {
        // Validated against the tokens the route accepts, so a typo fails the start instead of withdrawing nothing.
        RuleFor(options => options.DisabledFormats)
            .Must(formats => formats.All(format => ConversationExportFormatNames.TryParse(format, out _) && !string.IsNullOrWhiteSpace(format)))
            .WithMessage($"Export:DisabledFormats may only name {ConversationExportFormatNames.SupportedList}.");
    }
}
