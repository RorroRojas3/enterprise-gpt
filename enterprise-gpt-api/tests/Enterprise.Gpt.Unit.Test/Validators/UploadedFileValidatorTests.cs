using Microsoft.Extensions.Options;
using NSubstitute;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Validators;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Validators;

public sealed class UploadedFileValidatorTests
{
    private static readonly byte[] _pdfBytes = [.. "%PDF-1.7"u8.ToArray(), .. new byte[16]];
    private static readonly byte[] _ooxmlBytes = [0x50, 0x4B, 0x03, 0x04, .. new byte[16]];
    private static readonly byte[] _ole2Bytes = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[16]];

    private static UploadedFileValidator CreateValidator(long maxFileSizeBytes = 50L * 1024 * 1024)
    {
        var factory = Substitute.For<IDocumentTextExtractorFactory>();
        factory.SupportedExtensionNames.Returns([".csv", ".doc", ".docx", ".md", ".pdf", ".pptx", ".txt", ".xlsx"]);
        factory.IsSupported(Arg.Any<FileExtensions>()).Returns(call =>
            call.Arg<FileExtensions>() is FileExtensions.Csv or FileExtensions.Pdf or FileExtensions.Doc
                or FileExtensions.Docx or FileExtensions.Pptx or FileExtensions.Md or FileExtensions.Txt
                or FileExtensions.Xlsx);

        var options = Options.Create(new DocumentOptions { MaxFileSizeBytes = maxFileSizeBytes });

        return new UploadedFileValidator(options, factory);
    }

    private static FileDto File(string fileName, byte[] content)
    {
        return new FileDto { FileName = fileName, ContentType = "application/octet-stream", Length = content.Length, Content = content };
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("REPORT.PDF")]
    public void Validate_PdfWithMatchingSignature_IsValid(string fileName)
    {
        Assert.True(CreateValidator().Validate(File(fileName, _pdfBytes)).IsValid);
    }

    [Theory]
    [InlineData("report.docx")]
    [InlineData("deck.pptx")]
    [InlineData("budget.xlsx")]
    public void Validate_OoxmlWithMatchingSignature_IsValid(string fileName)
    {
        Assert.True(CreateValidator().Validate(File(fileName, _ooxmlBytes)).IsValid);
    }

    [Fact]
    public void Validate_LegacyDocWithMatchingSignature_IsValid()
    {
        Assert.True(CreateValidator().Validate(File("report.doc", _ole2Bytes)).IsValid);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("readme.md")]
    [InlineData("orders.csv")]
    public void Validate_TextFile_IsValid(string fileName)
    {
        Assert.True(CreateValidator().Validate(File(fileName, Encoding.UTF8.GetBytes("hello"))).IsValid);
    }

    [Fact]
    public void Validate_EmptyFile_IsRejected()
    {
        var result = CreateValidator().Validate(File("report.pdf", []));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_FileLargerThanTheConfiguredLimit_IsRejected()
    {
        var content = new byte[128];
        _pdfBytes.CopyTo(content, 0);

        var result = CreateValidator(maxFileSizeBytes: 64).Validate(File("report.pdf", content));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("maximum upload size", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("book.epub")]
    [InlineData("macros.xlsm")]
    [InlineData("legacy.xls")]
    [InlineData("archive.zip")]
    public void Validate_UnsupportedExtension_IsRejectedAndListsWhatIsSupported(string fileName)
    {
        var result = CreateValidator().Validate(File(fileName, _ooxmlBytes));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("not supported") && e.ErrorMessage.Contains(".pdf"));
    }

    [Fact]
    public void Validate_FileWithoutAnExtension_IsRejected()
    {
        var result = CreateValidator().Validate(File("report", _pdfBytes));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("not supported"));
    }

    [Fact]
    public void Validate_MissingFileName_IsRejected()
    {
        var result = CreateValidator().Validate(File(string.Empty, _pdfBytes));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(FileDto.FileName));
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("report.docx")]
    [InlineData("deck.pptx")]
    [InlineData("budget.xlsx")]
    [InlineData("report.doc")]
    public void Validate_ContentNotMatchingTheClaimedExtension_IsRejected(string fileName)
    {
        // The extension is caller-controlled, and every downstream reader assumes a particular container.
        var result = CreateValidator().Validate(File(fileName, Encoding.UTF8.GetBytes("just some text")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("do not match its extension"));
    }

    public static TheoryData<string, byte[]> TextEncodings() => new()
    {
        { "UTF-8", Encoding.UTF8.GetBytes("naïve café — 日本語") },
        { "UTF-8 with BOM", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("naïve café")] },
        { "UTF-16 LE with BOM", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("naïve café")] },
        { "UTF-16 BE with BOM", [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("naïve café")] },
        { "UTF-32 LE with BOM", [.. Encoding.UTF32.GetPreamble(), .. Encoding.UTF32.GetBytes("naïve café")] },
    };

    [Theory]
    [MemberData(nameof(TextEncodings))]
    public void Validate_CsvInAnyEncodingTheExtractorReads_IsAccepted(string encodingName, byte[] content)
    {
        var result = CreateValidator().Validate(File("orders.csv", content));

        Assert.True(result.IsValid, $"{encodingName} content was rejected: {string.Join("; ", result.Errors.Select(e => e.ErrorMessage))}");
    }

    [Theory]
    [MemberData(nameof(TextEncodings))]
    public void Validate_TextInAnyEncodingThePlainTextExtractorReads_IsAccepted(string encodingName, byte[] content)
    {
        // UTF-16 and UTF-32 carry a NUL in most bytes, so a naive "contains NUL means binary" probe would
        // reject every file Notepad saves as "Unicode" — files the extractor decodes correctly.
        var result = CreateValidator().Validate(File("notes.txt", content));

        Assert.True(result.IsValid, $"{encodingName} content was rejected: {string.Join("; ", result.Errors.Select(e => e.ErrorMessage))}");
    }

    [Theory]
    [InlineData("payload.txt")]
    [InlineData("payload.csv")]
    public void Validate_BinaryContentRenamedAsText_IsRejected(string fileName)
    {
        var content = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x1A, 0x0A };

        var result = CreateValidator().Validate(File(fileName, content));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("do not match its extension"));
    }

    [Fact]
    public void Validate_FileShorterThanTheExpectedSignature_IsRejected()
    {
        var result = CreateValidator().Validate(File("report.pdf", [0x25, 0x50]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("do not match its extension"));
    }
}
