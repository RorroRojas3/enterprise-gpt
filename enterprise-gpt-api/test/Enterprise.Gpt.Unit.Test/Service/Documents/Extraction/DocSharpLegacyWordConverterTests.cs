using System.Text;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Documents.Extraction;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Documents.Extraction;

/// <remarks>
/// Failure paths only: a valid OLE2 <c>.doc</c> cannot be synthesized in a test, so success is verified
/// manually. Malformed input must surface as a 400, not an opaque background-job failure.
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
