namespace Enterprise.Gpt.Api.Options;

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
    /// set. See <c>Service/Exports/Fonts/ExportFontResolver.cs</c>.
    /// </remarks>
    public string? FontDirectory { get; set; }
}
