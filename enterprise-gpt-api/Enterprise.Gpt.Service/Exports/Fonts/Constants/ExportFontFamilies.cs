using System.Runtime.InteropServices;
using PdfSharp.Fonts;

namespace Enterprise.Gpt.Service.Exports.Fonts.Constants;

/// <summary>
/// The two logical font families a PDF export draws with.
/// </summary>
public static class ExportFontFamilies
{
    /// <summary>The body and heading face.</summary>
    public const string Sans = "Export Sans";

    /// <summary>The face code blocks and inline code are set in.</summary>
    public const string Mono = "Export Mono";
}
