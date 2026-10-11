using Enterprise.Gpt.Service.Exports;
using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

/// <summary>
/// Conversation export configuration, bound from the <c>Export</c> section and validated at startup.
/// </summary>
public sealed class ExportOptions
{
    public const string SectionName = "Export";

    /// <summary>Formats this deployment refuses, as wire tokens (<c>docx</c>, <c>pdf</c>, …).</summary>
    /// <remarks>
    /// A disabled format's renderer is never registered, so the route answers with the
    /// <c>export-renderer-not-configured</c> problem.
    /// </remarks>
    public string[] DisabledFormats { get; set; } = [];

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
