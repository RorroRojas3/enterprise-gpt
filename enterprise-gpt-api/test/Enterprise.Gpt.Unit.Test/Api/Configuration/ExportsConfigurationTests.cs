using Enterprise.Gpt.Api.Configuration;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Exports;
using Enterprise.Gpt.Service.Exports.Fonts;
using Enterprise.Gpt.Service.Exports.Interfaces;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Configuration;

/// <remarks>
/// In the PDF collection: registering a PDF renderer writes the process-global
/// <c>GlobalFontSettings.FontResolver</c>.
/// </remarks>
[Collection(PdfFontCollection.Name)]
public sealed class ExportsConfigurationTests
{
    private static readonly string EmptyDirectory =
        Path.Combine(Path.GetTempPath(), $"export-fonts-none-{Guid.NewGuid():N}");

    private static List<ConversationExportFormats> Register(
        ExportOptions options, bool fontsUsable = true, bool templateExists = true)
    {
        var services = new ServiceCollection();

        ExportsConfiguration.AddExportRenderers(
            services,
            options,
            // A missing directory reads as unusable only on hosts without platform fonts; the factory
            // seam makes both outcomes reachable on any machine.
            _ => new ExportFontResolver(fontsUsable ? null : EmptyDirectory),
            () => templateExists);

        // Resolved through the container, which also proves the renderers' dependencies are satisfiable.
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

    [Theory]
    [InlineData("PDF")]
    [InlineData(" pdf ")]
    [InlineData("Pdf")]
    public void AddExportRenderers_DisabledFormatCasingAndPadding_StillWithdrawsIt(string disabled)
    {
        var formats = Register(new ExportOptions { DisabledFormats = [disabled] });

        Assert.DoesNotContain(ConversationExportFormats.Pdf, formats);
    }

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
    /// The template loads in a static initializer and renderers resolve as a collection, so a missing
    /// file would otherwise fault every format's first export.
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
}
