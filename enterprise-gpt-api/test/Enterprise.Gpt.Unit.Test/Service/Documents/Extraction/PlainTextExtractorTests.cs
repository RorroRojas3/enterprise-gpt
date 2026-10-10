using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Extraction;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Extraction;

public sealed class PlainTextExtractorTests
{
    private readonly PlainTextExtractor _extractor = new(NullLogger<PlainTextExtractor>.Instance);

    private static FileDto File(byte[] content, string fileName = "notes.txt")
    {
        return new FileDto { FileName = fileName, ContentType = "text/plain", Length = content.Length, Content = content };
    }

    [Fact]
    public void SupportedExtensions_ContainsMarkdownAndPlainText()
    {
        Assert.Equal([FileExtensions.Md, FileExtensions.Txt], _extractor.SupportedExtensions);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("readme.md")]
    public async Task ExtractAsync_TextFile_ReturnsOneSegmentWithoutASourceNumber(string fileName)
    {
        var content = Encoding.UTF8.GetBytes("The quick brown fox.");

        var segments = await _extractor.ExtractAsync(File(content, fileName), progress: null, TestContext.Current.CancellationToken);

        var segment = Assert.Single(segments);
        Assert.Equal("The quick brown fox.", segment.Text);
        Assert.Null(segment.SourceNumber);
    }

    [Fact]
    public async Task ExtractAsync_MarkdownFile_PreservesMarkup()
    {
        // The markup carries structure that helps both the embedding and the eventual answer, so it is
        // deliberately not stripped.
        var content = Encoding.UTF8.GetBytes("# Heading\n\n- item one\n- item two\n\n```csharp\nvar x = 1;\n```");

        var segments = await _extractor.ExtractAsync(File(content, "guide.md"), progress: null, TestContext.Current.CancellationToken);

        var text = Assert.Single(segments).Text;
        Assert.Contains("# Heading", text);
        Assert.Contains("- item one", text);
        Assert.Contains("```csharp", text);
    }

    [Fact]
    public async Task ExtractAsync_WindowsLineEndings_AreNormalized()
    {
        var content = Encoding.UTF8.GetBytes("line one\r\nline two\r\nline three");

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal("line one\nline two\nline three", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task ExtractAsync_Utf16WithByteOrderMark_IsDecodedCorrectly()
    {
        var content = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("naïve café")).ToArray();

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal("naïve café", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task ExtractAsync_Utf8WithoutByteOrderMark_IsDecodedCorrectly()
    {
        var content = Encoding.UTF8.GetBytes("naïve café — 日本語");

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal("naïve café — 日本語", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task ExtractAsync_WhitespaceOnlyFile_ReturnsNoSegments()
    {
        var content = Encoding.UTF8.GetBytes("   \n\n\t  ");

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        Assert.Empty(segments);
    }

    [Fact]
    public async Task ExtractAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _extractor.ExtractAsync(null!, progress: null, TestContext.Current.CancellationToken));
    }
}
