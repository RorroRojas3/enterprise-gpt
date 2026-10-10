using Enterprise.Gpt.Service.Exports.Fonts;
using PdfSharp.Fonts;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Installs one process-wide font resolver for every PDF test.
/// </summary>
public sealed class PdfFontFixture
{
    /// <summary>
    /// Why the PDF tests skip when they do — a machine with no font PDFsharp can read is exactly the
    /// deployment the <c>export-renderer-not-configured</c> problem exists for, so it is a skip
    /// rather than a failure.
    /// </summary>
    public const string SkipReason =
        "No usable export font was found on this machine, so PDF rendering is unavailable here — "
        + "the same condition that makes the route answer 503. See Export/Fonts/README.md.";

    public PdfFontFixture()
    {
        var resolver = new ExportFontResolver();
        IsUsable = resolver.IsUsable;

        if (IsUsable)
        {
            GlobalFontSettings.FontResolver = resolver;
        }
    }

    /// <summary>Gets whether this machine resolved a body face.</summary>
    public bool IsUsable { get; }
}
