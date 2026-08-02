using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Extraction;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Extraction;

public sealed class DocumentTextExtractorFactoryTests
{
    private static IDocumentTextExtractor Extractor(params FileExtensions[] extensions)
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.SupportedExtensions.Returns(extensions);
        return extractor;
    }

    private static DocumentTextExtractorFactory CreateFactory(params IDocumentTextExtractor[] extractors)
    {
        return new DocumentTextExtractorFactory(extractors, NullLogger<DocumentTextExtractorFactory>.Instance);
    }

    [Fact]
    public void Resolve_RegisteredExtension_ReturnsTheOwningExtractor()
    {
        var pdf = Extractor(FileExtensions.Pdf, FileExtensions.Docx);
        var pptx = Extractor(FileExtensions.Pptx);
        var factory = CreateFactory(pdf, pptx);

        Assert.Same(pdf, factory.Resolve(FileExtensions.Pdf));
        Assert.Same(pdf, factory.Resolve(FileExtensions.Docx));
        Assert.Same(pptx, factory.Resolve(FileExtensions.Pptx));
    }

    [Fact]
    public void Resolve_UnregisteredExtension_ThrowsValidationExceptionListingSupportedTypes()
    {
        var factory = CreateFactory(Extractor(FileExtensions.Pdf));

        var exception = Assert.Throws<ValidationException>(() => factory.Resolve(FileExtensions.Xlsx));

        var message = Assert.Single(exception.Errors).ErrorMessage;
        Assert.Contains(".xlsx", message);
        Assert.Contains(".pdf", message);
        Assert.Equal(nameof(FileDto.FileName), Assert.Single(exception.Errors).PropertyName);
    }

    [Fact]
    public void IsSupported_ReflectsTheRegisteredExtractors()
    {
        var factory = CreateFactory(Extractor(FileExtensions.Pdf), Extractor(FileExtensions.Md, FileExtensions.Txt));

        Assert.True(factory.IsSupported(FileExtensions.Pdf));
        Assert.True(factory.IsSupported(FileExtensions.Md));
        Assert.False(factory.IsSupported(FileExtensions.Csv));
    }

    [Fact]
    public void SupportedExtensionNames_ReturnsTheDottedFormOfEveryRegisteredExtension()
    {
        var factory = CreateFactory(Extractor(FileExtensions.Pdf, FileExtensions.Docx), Extractor(FileExtensions.Txt));

        Assert.Equal([".docx", ".pdf", ".txt"], factory.SupportedExtensionNames.Order());
    }

    [Fact]
    public void Constructor_TwoExtractorsClaimingTheSameExtension_ThrowsSoTheMistakeSurfacesAtStartup()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateFactory(Extractor(FileExtensions.Pdf), Extractor(FileExtensions.Pdf)));

        Assert.Contains(".pdf", exception.Message);
    }

    [Fact]
    public void Constructor_NoExtractors_ReportsNothingSupportedRatherThanThrowing()
    {
        var factory = CreateFactory();

        Assert.Empty(factory.SupportedExtensions);
        Assert.False(factory.IsSupported(FileExtensions.Pdf));
    }

    [Fact]
    public void Constructor_NullExtractors_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DocumentTextExtractorFactory(null!, NullLogger<DocumentTextExtractorFactory>.Instance));
    }
}
