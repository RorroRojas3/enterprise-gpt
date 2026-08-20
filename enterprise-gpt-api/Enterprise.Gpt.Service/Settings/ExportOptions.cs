namespace Enterprise.Gpt.Service.Settings;

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

/// <summary>
/// PDF rendering configuration, bound from the <c>Export:Pdf</c> section.
/// </summary>
public sealed class PdfExportOptions
{
    /// <summary>
    /// Directory to load export font faces from. Defaults to <c>Fonts</c> beside the assembly.
    /// </summary>
    /// <remarks>
    /// PDFsharp's cross-platform build resolves no font on its own and throws without a resolver,
    /// so a PDF export is only possible where faces can be found. The directory is searched first
    /// and the operating system's own font directories after it, which is what lets a developer
    /// machine work with nothing dropped in while a container is given an explicit, deterministic
    /// set. See <c>Export/Fonts/ExportFontResolver.cs</c>.
    /// </remarks>
    public string? FontDirectory { get; set; }
}
