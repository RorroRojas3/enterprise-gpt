using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Extraction;

public sealed class PresentationTextExtractorTests
{
    private readonly PresentationTextExtractor _extractor = new(NullLogger<PresentationTextExtractor>.Instance);

    private static FileDto File(byte[] content, string fileName = "deck.pptx")
    {
        return new FileDto
        {
            FileName = fileName,
            ContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            Length = content.Length,
            Content = content
        };
    }

    [Fact]
    public void SupportedExtensions_ContainsOnlyPptx()
    {
        Assert.Equal([FileExtensions.Pptx], _extractor.SupportedExtensions);
    }

    [Fact]
    public async Task ExtractAsync_Presentation_ReturnsOneSegmentPerSlideInPresentationOrder()
    {
        var content = PresentationBuilder.Create(
        [
            ["First slide title", "First slide body"],
            ["Second slide title"],
            ["Third slide title"]
        ]);

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(3, segments.Count);
        Assert.Equal([1, 2, 3], segments.Select(s => s.SourceNumber));
        Assert.Contains("First slide title", segments[0].Text);
        Assert.Contains("First slide body", segments[0].Text);
        Assert.Contains("Second slide title", segments[1].Text);
        Assert.Contains("Third slide title", segments[2].Text);
    }

    [Fact]
    public async Task ExtractAsync_SlideWithSpeakerNotes_IncludesTheNotes()
    {
        var content = PresentationBuilder.Create(
            [["Visible title"]],
            new Dictionary<int, string> { [0] = "Remember to mention the migration timeline" });

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        var segment = Assert.Single(segments);
        Assert.Contains("Visible title", segment.Text);
        Assert.Contains("Remember to mention the migration timeline", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_SlideWithSpeakerNotes_ExcludesTheSlideNumberPlaceholder()
    {
        // The notes slide carries a slide-number placeholder whose text is just the number; embedding it
        // would add noise to every chunk that includes notes.
        var content = PresentationBuilder.Create(
            [["Visible title"]],
            new Dictionary<int, string> { [0] = "Actual notes" });

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("7", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task ExtractAsync_SlideWithoutText_IsOmitted()
    {
        var content = PresentationBuilder.Create([["Has text"], [], ["Also has text"]]);

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, segments.Count);
        Assert.Equal([1, 3], segments.Select(s => s.SourceNumber));
    }

    [Fact]
    public async Task ExtractAsync_Presentation_ReportsProgressForEverySlide()
    {
        var content = PresentationBuilder.Create([["One"], ["Two"], ["Three"]]);
        var reports = new List<DocumentExtractionProgress>();
        var progress = new CollectingProgress<DocumentExtractionProgress>(reports);

        await _extractor.ExtractAsync(File(content), progress, TestContext.Current.CancellationToken);

        Assert.Equal(3, reports.Count);
        Assert.Equal(1d, reports[^1].Fraction);
        Assert.All(reports, report => Assert.False(string.IsNullOrWhiteSpace(report.Message)));
    }

    [Fact]
    public async Task ExtractAsync_ContentThatIsNotAPresentation_ThrowsValidationException()
    {
        var content = "this is not a presentation"u8.ToArray();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExtractAsync_SlideIdPointingAtAMissingPart_ThrowsValidationException()
    {
        // The package opens cleanly and only fails when the dangling relationship is dereferenced. Guarding
        // Open() alone would let this escape as an opaque 500-class job failure instead of a 400.
        var content = PresentationBuilder.CreateWithDanglingSlideRelationship();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExtractAsync_SlidePartContainingMalformedXml_ThrowsValidationException()
    {
        // Part XML is parsed lazily, so this also throws long after Open() has returned.
        var content = PresentationBuilder.CreateWithCorruptSlideXml();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExtractAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _extractor.ExtractAsync(null!, progress: null, TestContext.Current.CancellationToken));
    }
}
