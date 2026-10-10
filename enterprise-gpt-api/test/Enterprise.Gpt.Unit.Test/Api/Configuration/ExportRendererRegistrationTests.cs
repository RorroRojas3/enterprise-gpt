using Enterprise.Gpt.Api.Export;
using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Export.Fonts;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Export;

/// <summary>
/// Which formats a deployment offers is observable behaviour — an absent renderer is a <c>503</c>
/// carrying the <c>export-renderer-not-configured</c> type — so the configuration that decides it is
/// worth pinning.
/// </summary>
/// <remarks>
/// In the PDF collection because registering a usable PDF renderer assigns
/// <c>GlobalFontSettings.FontResolver</c>, which is process-global. Every resolver here reads the
/// same files so a race would not change any outcome, but a test that writes shared runtime state
/// should not be doing it concurrently with the tests that read it.
/// </remarks>
[Collection(PdfFontCollection.Name)]
public sealed class ExportRendererRegistrationTests
{
    private static readonly string EmptyDirectory =
        Path.Combine(Path.GetTempPath(), $"export-fonts-none-{Guid.NewGuid():N}");

    private static List<ConversationExportFormats> Register(
        ExportOptions options, bool fontsUsable = true, bool templateExists = true)
    {
        var services = new ServiceCollection();

        ExportRendererRegistration.AddExportRenderers(
            services,
            options,
            // A resolver pointed at a directory that does not exist reports itself unusable only when
            // the host has no platform fonts either, so "usable" is arranged by pointing at a real
            // one — the factory seam is what makes both outcomes reachable on any machine.
            _ => new ExportFontResolver(fontsUsable ? null : EmptyDirectory),
            () => templateExists);

        // Resolved through the container rather than read off the descriptors: the registration is
        // what the service consumes, and building it proves the renderers' own dependencies are
        // satisfiable too.
        services.AddSingleton<IMarkdownBlockMapper, MarkdownBlockMapper>();
        using var provider = services.BuildServiceProvider();

        return [.. provider.GetRequiredService<IEnumerable<IConversationExportRenderer>>()
            .Select(renderer => renderer.Format)];
    }

    [Fact]
    public void AddExportRenderers_DefaultOptions_RegistersEveryTextFormat()
    {
        var formats = Register(new ExportOptions());

        Assert.Contains(ConversationExportFormats.Html, formats);
        Assert.Contains(ConversationExportFormats.Json, formats);
        Assert.Contains(ConversationExportFormats.Markdown, formats);
        Assert.Contains(ConversationExportFormats.Docx, formats);
    }

    [Theory]
    [InlineData("md", ConversationExportFormats.Markdown)]
    [InlineData("docx", ConversationExportFormats.Docx)]
    [InlineData("json", ConversationExportFormats.Json)]
    [InlineData("html", ConversationExportFormats.Html)]
    public void AddExportRenderers_DisabledFormat_DoesNotRegisterIt(
        string disabled, ConversationExportFormats format)
    {
        var formats = Register(new ExportOptions { DisabledFormats = [disabled] });

        Assert.DoesNotContain(format, formats);
    }

    /// <summary>
    /// Configuration is hand-written, so a value that differs from the wire token only in casing or
    /// stray whitespace has to withdraw the format rather than silently do nothing.
    /// </summary>
    [Theory]
    [InlineData("PDF")]
    [InlineData(" pdf ")]
    [InlineData("Pdf")]
    public void AddExportRenderers_DisabledFormatCasingAndPadding_StillWithdrawsIt(string disabled)
    {
        var formats = Register(new ExportOptions { DisabledFormats = [disabled] });

        Assert.DoesNotContain(ConversationExportFormats.Pdf, formats);
    }

    /// <summary>
    /// The condition the typed 503 exists for: a deployment that can run everything else
    /// but cannot draw a glyph.
    /// </summary>
    [Fact]
    public void AddExportRenderers_NoUsableFont_RegistersEveryFormatExceptPdf()
    {
        Assert.SkipWhen(
            new ExportFontResolver(EmptyDirectory).IsUsable,
            "This machine has platform fonts the resolver legitimately falls back to.");

        var formats = Register(new ExportOptions(), fontsUsable: false);

        Assert.DoesNotContain(ConversationExportFormats.Pdf, formats);
        Assert.Contains(ConversationExportFormats.Docx, formats);
    }

    /// <summary>
    /// The template loads from a static initializer and the renderers resolve as a collection, so a
    /// missing file would otherwise fault the first export of every format, not just HTML.
    /// </summary>
    [Fact]
    public void AddExportRenderers_MissingTemplate_WithholdsOnlyHtml()
    {
        var formats = Register(new ExportOptions(), templateExists: false);

        Assert.DoesNotContain(ConversationExportFormats.Html, formats);
        Assert.Contains(ConversationExportFormats.Json, formats);
        Assert.Contains(ConversationExportFormats.Markdown, formats);
        Assert.Contains(ConversationExportFormats.Docx, formats);
    }

    [Fact]
    public void AddExportRenderers_EveryFormatDisabled_RegistersNothing()
    {
        var formats = Register(new ExportOptions
        {
            DisabledFormats = [.. ConversationExportFormatNames.Supported]
        });

        Assert.Empty(formats);
    }

    [Theory]
    [InlineData(ConversationExportFormats.Pdf, "pdf")]
    [InlineData(ConversationExportFormats.Docx, "docx")]
    public void IsEnabled_FormatNamedInDisabledFormats_IsFalse(
        ConversationExportFormats format, string token)
    {
        Assert.False(
            ExportRendererRegistration.IsEnabled(new ExportOptions { DisabledFormats = [token] }, format));
        Assert.True(ExportRendererRegistration.IsEnabled(new ExportOptions(), format));
    }
}
