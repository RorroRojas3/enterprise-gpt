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
    /// Searched before the OS font directories, so a developer machine needs nothing while a container
    /// gets a deterministic set. See <c>Service/Exports/Fonts/ExportFontResolver.cs</c>.
    /// </remarks>
    public string? FontDirectory { get; set; }
}
