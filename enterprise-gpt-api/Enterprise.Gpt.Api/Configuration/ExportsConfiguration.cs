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
/// Which renderers register is observable: a format with none answers <c>503</c> with the
/// <c>export-renderer-not-configured</c> problem, and <see cref="ExportAvailabilityLogger"/> logs the set.
/// </summary>
internal static class ExportsConfiguration
{
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

    /// <summary>
    /// Adds one <see cref="IConversationExportRenderer"/> per format this deployment can render.
    /// </summary>
    /// <remarks>
    /// The font and template probes are injectable so tests do not depend on the host's installed faces.
    /// </remarks>
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

        // GlobalFontSettings.FontResolver is process-global and set-once; resolving at composition makes
        // PDF support a startup answer. Overwriting it from a second host is harmless: same files.
        var fontResolver = resolveFonts(options.Pdf.FontDirectory);

        if (!fontResolver.IsUsable)
        {
            return;
        }

        GlobalFontSettings.FontResolver = fontResolver;
        services.AddSingleton<IConversationExportRenderer, PdfExportRenderer>();
    }

    /// <summary>Whether the format is absent from <c>Export:DisabledFormats</c>.</summary>
    public static bool IsEnabled(ExportOptions options, ConversationExportFormats format) =>
        !options.DisabledFormats.Any(disabled =>
            string.Equals(disabled?.Trim(), format.ToWireFormat(), StringComparison.OrdinalIgnoreCase));

    #endregion
}
