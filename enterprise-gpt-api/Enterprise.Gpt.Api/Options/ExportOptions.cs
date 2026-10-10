namespace Enterprise.Gpt.Service.Options;

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
