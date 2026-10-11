using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Exports;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Exports;

/// <summary>
/// The parse lookup, the supported set and <see cref="ConversationExportFormatNames.ToWireFormat"/> all
/// list the wire tokens, and only this keeps them in step; drift fails silently.
/// </summary>
public sealed class ExportFormatNamesTests
{
    [Fact]
    public void Supported_ListsEveryFormatsWireToken()
    {
        Assert.Equal(
            Enum.GetValues<ConversationExportFormats>().Select(format => format.ToWireFormat()).Order(),
            ConversationExportFormatNames.Supported.Order());
    }

    [Fact]
    public void TryParse_EverySupportedToken_ResolvesBackToTheFormatThatNamedIt()
    {
        foreach (var format in Enum.GetValues<ConversationExportFormats>())
        {
            Assert.True(ConversationExportFormatNames.TryParse(format.ToWireFormat(), out var parsed));
            Assert.Equal(format, parsed);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_NoFormat_DefaultsToHtml(string? format)
    {
        Assert.True(ConversationExportFormatNames.TryParse(format, out var parsed));
        Assert.Equal(ConversationExportFormats.Html, parsed);
    }

    [Theory]
    [InlineData("rtf")]
    [InlineData("markdown")]
    [InlineData("doc")]
    [InlineData("htm")]
    public void TryParse_UnsupportedToken_IsRejected(string format)
    {
        Assert.False(ConversationExportFormatNames.TryParse(format, out _));
    }

    [Fact]
    public void SupportedList_QuotesEverySupportedToken()
    {
        foreach (var token in ConversationExportFormatNames.Supported)
        {
            Assert.Contains($"'{token}'", ConversationExportFormatNames.SupportedList, StringComparison.Ordinal);
        }
    }
}
