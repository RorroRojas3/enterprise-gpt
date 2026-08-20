using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Export.Fonts;
using Enterprise.Gpt.Service.Export.Renderers;
using Enterprise.Gpt.Service.Settings;
using PdfSharp.Fonts;

namespace Enterprise.Gpt.Api.Export;

/// <summary>
/// Registers the conversation export renderers this deployment can actually use.
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than a local function in <c>Program.cs</c> because what it decides is a
/// contract, not plumbing: a format with no renderer answers <c>503</c> with the
/// <c>export-renderer-not-configured</c> problem, so "which formats does this deployment offer" is
/// observable behaviour that deserves tests of its own.
/// </para>
/// <para>
/// Two of the five formats can be absent, for different reasons. Any of them can be withdrawn by
/// <c>Export:DisabledFormats</c>. PDF additionally needs a font — PDFsharp's cross-platform build
/// reads none from the operating system on its own and throws on the first glyph without a resolver
/// — and HTML needs its template file. Both are resolved here, at composition, so the route answers
/// up front rather than faulting halfway into a response body.
/// </para>
/// <para>
/// It logs nothing. The decisions are made before any logging pipeline exists, and
/// <see cref="ExportAvailabilityLogger"/> reports them at startup from the built container instead —
/// which is what keeps them out of a throwaway <c>LoggerFactory</c> and inside the pipeline an
/// operator actually queries.
/// </para>
/// </remarks>
internal static class ExportRendererRegistration
{
    /// <summary>
    /// Adds one <see cref="IConversationExportRenderer"/> per format this deployment can render.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The bound export options.</param>
    /// <param name="fontResolverFactory">
    /// Builds the PDF font resolver. Injectable so a test can exercise both the usable and unusable
    /// deployments without depending on which faces the host machine happens to have installed.
    /// </param>
    /// <param name="templateExists">
    /// Whether the HTML export template is present. Injectable for the same reason.
    /// </param>
    public static void AddExportRenderers(
        IServiceCollection services,
        ExportOptions options,
        Func<string?, ExportFontResolver>? fontResolverFactory = null,
        Func<bool>? templateExists = null)
    {
        var resolveFonts = fontResolverFactory ?? (directory => new ExportFontResolver(directory));
        var hasTemplate = templateExists ?? (() => File.Exists(HtmlExportRenderer.TemplatePath));

        if (hasTemplate())
        {
            Register<HtmlExportRenderer>(services, options, ConversationExportFormats.Html);
        }

        Register<JsonExportRenderer>(services, options, ConversationExportFormats.Json);
        Register<MarkdownExportRenderer>(services, options, ConversationExportFormats.Markdown);
        Register<WordExportRenderer>(services, options, ConversationExportFormats.Docx);

        if (!IsEnabled(options, ConversationExportFormats.Pdf))
        {
            return;
        }

        // GlobalFontSettings.FontResolver is process-global and documented as set-once, before any
        // font operation. Building the resolver here — at composition, not on the first export — is
        // what makes "can this deployment render a PDF?" a startup answer rather than a per-request
        // surprise. Every instance resolves the same files, so a second host in one process
        // overwriting this is benign.
        var fontResolver = resolveFonts(options.Pdf.FontDirectory);

        if (!fontResolver.IsUsable)
        {
            return;
        }

        GlobalFontSettings.FontResolver = fontResolver;
        services.AddSingleton<IConversationExportRenderer, PdfExportRenderer>();
    }

    /// <summary>
    /// Whether a format is offered by this deployment's configuration.
    /// </summary>
    /// <param name="options">The bound export options.</param>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> unless the format is named in <c>Export:DisabledFormats</c>.</returns>
    public static bool IsEnabled(ExportOptions options, ConversationExportFormats format) =>
        !options.DisabledFormats.Any(disabled =>
            string.Equals(disabled?.Trim(), format.ToWireFormat(), StringComparison.OrdinalIgnoreCase));

    private static void Register<TRenderer>(
        IServiceCollection services, ExportOptions options, ConversationExportFormats format)
        where TRenderer : class, IConversationExportRenderer
    {
        if (IsEnabled(options, format))
        {
            services.AddSingleton<IConversationExportRenderer, TRenderer>();
        }
    }
}
