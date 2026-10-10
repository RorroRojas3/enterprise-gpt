using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Converters;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Converters;

/// <summary>
/// Covers the failure behavior of the legacy <c>.doc</c> converter.
/// </summary>
/// <remarks>
/// The successful conversion path is not covered here: a valid Word 97–2003 file is an OLE2 compound
/// document that cannot be synthesized in a test, and committing a binary fixture would be opaque. That
/// path is exercised by uploading a real <c>.doc</c> during manual verification. What matters most for
/// correctness is covered: malformed input must surface as a caller-fixable 400 rather than a 500, because
/// the converter runs on a background job where an unhandled exception becomes an opaque job failure.
/// </remarks>
public sealed class DocSharpLegacyWordConverterTests
{
    private readonly DocSharpLegacyWordConverter _converter = new(NullLogger<DocSharpLegacyWordConverter>.Instance);

    [Theory]
    [InlineData("this is plain text, not a compound document")]
    [InlineData("PK an ooxml package wearing a .doc extension")]
    public async Task ConvertToDocxAsync_ContentThatIsNotALegacyWordDocument_ThrowsValidationException(string content)
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _converter.ConvertToDocxAsync(Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken));

        var failure = Assert.Single(exception.Errors);
        Assert.Equal(nameof(FileDto.FileName), failure.PropertyName);
        Assert.Contains(".docx", failure.ErrorMessage);
    }

    [Fact]
    public async Task ConvertToDocxAsync_TruncatedOle2Header_ThrowsValidationException()
    {
        // Passes the magic-byte check the upload validator applies, then fails to parse.
        byte[] content = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00];

        await Assert.ThrowsAsync<ValidationException>(
            () => _converter.ConvertToDocxAsync(content, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConvertToDocxAsync_EmptyContent_ThrowsValidationException()
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => _converter.ConvertToDocxAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConvertToDocxAsync_NullContent_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _converter.ConvertToDocxAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConvertToDocxAsync_AlreadyCancelled_ThrowsOperationCanceledRatherThanValidation()
    {
        // A cancelled job is not a malformed document; conflating them would report shutdown as bad input.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _converter.ConvertToDocxAsync(Encoding.UTF8.GetBytes("anything"), cts.Token));
    }
}
