using Azure;
using Azure.AI.DocumentIntelligence;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Converters;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Extraction;

public sealed class DocumentIntelligenceTextExtractorTests
{
    private readonly IDocumentIntelligenceService _documentIntelligence = Substitute.For<IDocumentIntelligenceService>();
    private readonly ILegacyWordConverter _legacyWordConverter = Substitute.For<ILegacyWordConverter>();
    private readonly DocumentIntelligenceTextExtractor _extractor;

    public DocumentIntelligenceTextExtractorTests()
    {
        _extractor = new DocumentIntelligenceTextExtractor(
            NullLogger<DocumentIntelligenceTextExtractor>.Instance, _documentIntelligence, _legacyWordConverter);
    }

    private static FileDto File(string fileName = "report.pdf", string content = "%PDF-1.7")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FileDto { FileName = fileName, ContentType = "application/pdf", Length = bytes.Length, Content = bytes };
    }

    /// <summary>
    /// Builds an <see cref="AnalyzeResult"/> through the SDK's model factory, which is the only way to
    /// construct these sealed response types outside the client.
    /// </summary>
    private static AnalyzeResult AnalyzeResult(string content, params string[][] pageLines)
    {
        var pages = pageLines.Select((lines, i) => DocumentIntelligenceModelFactory.DocumentPage(
            pageNumber: i + 1,
            lines: lines.Select(line => DocumentIntelligenceModelFactory.DocumentLine(content: line)).ToList()));

        return DocumentIntelligenceModelFactory.AnalyzeResult(content: content, pages: pages);
    }

    private void SetUpAnalysis(AnalyzeResult result)
    {
        _documentIntelligence.ReadAsync(Arg.Any<byte[]>(), Arg.Any<Action<TimeSpan>?>(), Arg.Any<CancellationToken>())
            .Returns(result);
    }

    [Fact]
    public void SupportedExtensions_CoversEveryWordAndPdfFormat()
    {
        Assert.Equal([FileExtensions.Pdf, FileExtensions.Doc, FileExtensions.Docx], _extractor.SupportedExtensions);
    }

    [Fact]
    public async Task ExtractAsync_PaginatedResult_ReturnsOneSegmentPerPageWithItsPageNumber()
    {
        SetUpAnalysis(AnalyzeResult("all the content", ["page one line one", "page one line two"], ["page two line one"]));

        var segments = await _extractor.ExtractAsync(File(), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, segments.Count);
        Assert.Equal([1, 2], segments.Select(s => s.SourceNumber));
        Assert.Equal("page one line one\npage one line two", segments[0].Text);
        Assert.Equal("page two line one", segments[1].Text);
    }

    [Fact]
    public async Task ExtractAsync_PageWithoutText_IsOmitted()
    {
        SetUpAnalysis(AnalyzeResult("content", ["has text"], [], ["also has text"]));

        var segments = await _extractor.ExtractAsync(File(), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, segments.Count);
        Assert.Equal([1, 3], segments.Select(s => s.SourceNumber));
    }

    [Fact]
    public async Task ExtractAsync_ResultWithNoPages_FallsBackToTheFlatContent()
    {
        // Office inputs are not always paginated by the service; without this fallback a .docx could
        // silently produce zero chunks.
        SetUpAnalysis(AnalyzeResult("the whole document as one blob"));

        var segments = await _extractor.ExtractAsync(File("report.docx"), progress: null, TestContext.Current.CancellationToken);

        var segment = Assert.Single(segments);
        Assert.Equal("the whole document as one blob", segment.Text);
        Assert.Null(segment.SourceNumber);
    }

    [Fact]
    public async Task ExtractAsync_ResultWithNeitherPagesNorContent_ReturnsNoSegments()
    {
        SetUpAnalysis(AnalyzeResult(string.Empty));

        var segments = await _extractor.ExtractAsync(File(), progress: null, TestContext.Current.CancellationToken);

        Assert.Empty(segments);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(415)]
    public async Task ExtractAsync_ServiceRejectsTheDocument_ThrowsValidationException(int status)
    {
        // The service refused the document itself, which is the caller's problem to fix — a 400, not a 500.
        _documentIntelligence.ReadAsync(Arg.Any<byte[]>(), Arg.Any<Action<TimeSpan>?>(), Arg.Any<CancellationToken>())
            .Throws(new RequestFailedException(status, "unsupported or corrupt"));

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _extractor.ExtractAsync(File(), progress: null, TestContext.Current.CancellationToken));

        Assert.Contains("could not be read", Assert.Single(exception.Errors).ErrorMessage);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task ExtractAsync_ServiceFailsForAReasonTheCallerCannotFix_PropagatesTheOriginalFault(int status)
    {
        // Throttling and outages are not the document's fault; reporting them as bad input would tell the
        // user to fix a file that is fine.
        _documentIntelligence.ReadAsync(Arg.Any<byte[]>(), Arg.Any<Action<TimeSpan>?>(), Arg.Any<CancellationToken>())
            .Throws(new RequestFailedException(status, "service unavailable"));

        await Assert.ThrowsAsync<RequestFailedException>(
            () => _extractor.ExtractAsync(File(), progress: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExtractAsync_LegacyDocument_IsConvertedToDocxBeforeAnalysis()
    {
        // Document Intelligence does not accept the binary .doc container, so .doc takes the same OCR path
        // only after conversion.
        var converted = Encoding.UTF8.GetBytes("PK converted docx");
        _legacyWordConverter.ConvertToDocxAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(converted);
        SetUpAnalysis(AnalyzeResult("converted content", ["a line"]));

        var segments = await _extractor.ExtractAsync(
            File("report.doc", "ÐÏà"), progress: null, TestContext.Current.CancellationToken);

        await _legacyWordConverter.Received(1).ConvertToDocxAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _documentIntelligence.Received(1).ReadAsync(converted, Arg.Any<Action<TimeSpan>?>(), Arg.Any<CancellationToken>());
        Assert.Single(segments);
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("report.docx")]
    public async Task ExtractAsync_FormatTheServiceAcceptsDirectly_IsNotConverted(string fileName)
    {
        SetUpAnalysis(AnalyzeResult("content", ["a line"]));

        await _extractor.ExtractAsync(File(fileName), progress: null, TestContext.Current.CancellationToken);

        await _legacyWordConverter.DidNotReceive().ConvertToDocxAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_AnalysisHeartbeat_IsReportedAsProgressWithoutClaimingAPercentage()
    {
        // A remote analysis is opaque until it returns, so the heartbeat refreshes the message while the
        // fraction stays null — the percentage must never claim progress it cannot know.
        _documentIntelligence.ReadAsync(Arg.Any<byte[]>(), Arg.Any<Action<TimeSpan>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<Action<TimeSpan>?>()?.Invoke(TimeSpan.FromSeconds(8));
                call.Arg<Action<TimeSpan>?>()?.Invoke(TimeSpan.FromSeconds(75));
                return AnalyzeResult("content", ["a line"]);
            });

        var reports = new List<DocumentExtractionProgress>();
        await _extractor.ExtractAsync(File(), new CollectingProgress<DocumentExtractionProgress>(reports), TestContext.Current.CancellationToken);

        Assert.Contains(reports, r => r.Fraction is null && r.Message.Contains("8s"));
        Assert.Contains(reports, r => r.Fraction is null && r.Message.Contains("1m 15s"));
        Assert.Equal(1d, reports[^1].Fraction);
    }

    [Fact]
    public async Task ExtractAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _extractor.ExtractAsync(null!, progress: null, TestContext.Current.CancellationToken));
    }
}
