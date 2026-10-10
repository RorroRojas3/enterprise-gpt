using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Api.Startup;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Exports;
using Enterprise.Gpt.Service.Exports.Fonts;
using Enterprise.Gpt.Service.Exports.Interfaces;
using Enterprise.Gpt.Service.Exports.Renderers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdfSharp.Fonts;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers conversation export: the export service and the renderers this deployment can actually use.
/// </summary>
/// <remarks>
/// A format with no renderer answers <c>503</c> with the <c>export-renderer-not-configured</c> problem, so which
/// renderers register is observable behaviour; <see cref="ExportAvailabilityLogger"/> reports it once the host starts.
/// </remarks>
internal static class ExportsConfiguration
{
    /// <summary>
    /// Registers the export options, the export service and the available renderers.
    /// </summary>
    internal static IServiceCollection AddExports(this IServiceCollection services, IConfiguration configuration)
    {
        // DisabledFormats is validated against the tokens the route accepts, so a typo fails the start
        // rather than withdrawing nothing.
        services.AddSingleton<IValidator<ExportOptions>, ExportOptionsValidator>();
        services.AddOptions<ExportOptions>()
            .Bind(configuration.GetSection(ExportOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IMarkdownBlockMapper, MarkdownBlockMapper>();

        // Read eagerly: which renderers exist is decided at composition, before the container can resolve options.
        AddExportRenderers(
            services,
            configuration.GetSection(ExportOptions.SectionName).Get<ExportOptions>() ?? new ExportOptions());
        services.AddHostedService<ExportAvailabilityLogger>();

        services.AddScoped<IConversationExportService, ConversationExportService>();

        return services;
    }

    #region Private methods

    private static void Register<TRenderer>(
        IServiceCollection services, ExportOptions options, ConversationExportFormats format)
        where TRenderer : class, IConversationExportRenderer
    {
        if (IsEnabled(options, format))
        {
            services.AddSingleton<IConversationExportRenderer, TRenderer>();
        }
    }

    #endregion

    #region Public static methods

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

    #endregion
}
