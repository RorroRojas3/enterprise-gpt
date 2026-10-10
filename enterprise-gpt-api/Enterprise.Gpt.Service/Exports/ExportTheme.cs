using System.Globalization;

namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// The one palette, type scale and font stack every export format renders with.
/// </summary>
/// <remarks>
/// Transcribed from the application's light theme in <c>enterprise-gpt-ui/src/styles/_tokens.scss</c>,
/// which is named beside each colour. Exports are single-theme because a printed document has no
/// <c>prefers-color-scheme</c> to read.
/// </remarks>
public static class ExportTheme
{
    /// <summary>
    /// The palette, as six-digit hex with no leading <c>#</c>.
    /// </summary>
    /// <remarks>
    /// The form Open XML's <c>w:color</c> and MigraDoc's <c>Color</c> constructor both take; the HTML
    /// renderer prepends the <c>#</c> itself.
    /// </remarks>
    public static class Colors
    {
        /// <summary>The page behind the document. <c>--bs-body-bg</c>.</summary>
        public const string PageBackground = "F7FAFC";

        /// <summary>The document's own ground. <c>--surface</c>.</summary>
        public const string Surface = "FFFFFF";

        /// <summary>Code blocks and other inset panels. <c>--surface-2</c>.</summary>
        public const string SurfaceAlt = "EFF4F9";

        /// <summary>Body text. <c>--bs-body-color</c>.</summary>
        public const string Text = "16283A";

        /// <summary>Secondary text — the export timestamp, a code fence's language. <c>--muted</c>.</summary>
        public const string Muted = "5A6E82";

        /// <summary>Rules, table borders, the edge of a code block. <c>--bs-border-color</c>.</summary>
        public const string Border = "DCE5EE";

        /// <summary>Headings and role labels. <c>--brand</c>.</summary>
        public const string Brand = "14324F";

        /// <summary>Hyperlinks. <c>--bs-link-color</c>.</summary>
        public const string Link = "14324F";

        /// <summary>The tint behind a prompt, standing in for the app's chat bubble. <c>--think-bg</c>.</summary>
        public const string UserBand = "EAF1F8";
    }

    /// <summary>
    /// The faces each format draws with.
    /// </summary>
    public static class Fonts
    {
        /// <summary>The body and heading stack, most preferred first.</summary>
        public static IReadOnlyList<string> SansStack { get; } =
            ["Inter", "Calibri", "Segoe UI", "system-ui", "sans-serif"];

        /// <summary>The code stack, most preferred first.</summary>
        public static IReadOnlyList<string> MonoStack { get; } =
            ["JetBrains Mono", "Consolas", "ui-monospace", "monospace"];

        /// <summary>The body face a Word export names.</summary>
        /// <remarks>
        /// A .docx carries no glyphs and Word's substitution for a family the reader lacks is not
        /// deterministic, so the two Word faces are named from what ships with Office rather than from
        /// the head of the stack above.
        /// </remarks>
        public const string WordSans = "Calibri";

        /// <summary>The code face a Word export names.</summary>
        public const string WordMono = "Consolas";
    }

    /// <summary>
    /// Type sizes, in points.
    /// </summary>
    /// <remarks>
    /// Points because it is the one unit all three renderings convert from without rounding away a
    /// distinction: CSS takes <c>pt</c> directly, Open XML doubles it into half-points, MigraDoc reads
    /// it as-is.
    /// </remarks>
    public static class Sizes
    {
        /// <summary>Running text.</summary>
        public const double Body = 10.5;

        /// <summary>Code blocks and inline code.</summary>
        public const double Code = 9;

        /// <summary>The language named above a fenced code block.</summary>
        public const double CodeLanguage = 8;

        /// <summary>Text inside a table cell.</summary>
        public const double TableCell = 9.5;

        /// <summary>The conversation's name.</summary>
        public const double Title = 20;

        /// <summary>The export timestamp under the title.</summary>
        public const double Subtitle = 9;

        /// <summary>The <c>You</c> / <c>Assistant</c> label above each message.</summary>
        public const double RoleLabel = 11;

        private static readonly double[] Headings = [16.5, 15, 13.5, 12, 11, 11];

        /// <summary>
        /// Gets the size of a markdown heading.
        /// </summary>
        /// <param name="level">The heading level, 1 through 6. Values outside that range are clamped.</param>
        /// <returns>The size in points.</returns>
        public static double Heading(int level) => Headings[Math.Clamp(level, 1, 6) - 1];
    }

    /// <summary>
    /// Gets the custom properties the HTML export's template resolves every value against.
    /// </summary>
    /// <remarks>
    /// The template carries no literal of its own, so this list is the only place an export's
    /// appearance is stated — which is what keeps the HTML, Word and PDF documents in step.
    /// </remarks>
    public static IReadOnlyList<KeyValuePair<string, string>> CssVariables { get; } =
    [
        new("--page-bg", Hex(Colors.PageBackground)),
        new("--surface", Hex(Colors.Surface)),
        new("--surface-alt", Hex(Colors.SurfaceAlt)),
        new("--text", Hex(Colors.Text)),
        new("--muted", Hex(Colors.Muted)),
        new("--border", Hex(Colors.Border)),
        new("--brand", Hex(Colors.Brand)),
        new("--link", Hex(Colors.Link)),
        new("--user-band", Hex(Colors.UserBand)),
        new("--font-sans", Stack(Fonts.SansStack)),
        new("--font-mono", Stack(Fonts.MonoStack)),
        new("--size-body", Points(Sizes.Body)),
        new("--size-code", Points(Sizes.Code)),
        new("--size-code-lang", Points(Sizes.CodeLanguage)),
        new("--size-cell", Points(Sizes.TableCell)),
        new("--size-title", Points(Sizes.Title)),
        new("--size-subtitle", Points(Sizes.Subtitle)),
        new("--size-role", Points(Sizes.RoleLabel)),
        new("--size-h1", Points(Sizes.Heading(1))),
        new("--size-h2", Points(Sizes.Heading(2))),
        new("--size-h3", Points(Sizes.Heading(3))),
        new("--size-h4", Points(Sizes.Heading(4))),
        new("--size-h5", Points(Sizes.Heading(5))),
        new("--size-h6", Points(Sizes.Heading(6)))
    ];

    private static string Hex(string color) => $"#{color}";

    private static string Points(double size) =>
        $"{size.ToString("0.##", CultureInfo.InvariantCulture)}pt";

    // A family name containing a space has to be quoted; a generic keyword such as sans-serif must
    // not be, or it is read as a family nobody has.
    private static string Stack(IReadOnlyList<string> families) =>
        string.Join(", ", families.Select(family =>
            family.Contains(' ', StringComparison.Ordinal) ? $"'{family}'" : family));
}
