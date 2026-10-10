using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Options;

/// <summary>
/// The parent validators reach into <c>Documents:Chunking</c> and <c>Sheets:RowWindow</c>, which the
/// startup check enforces only through <c>SetValidator</c>.
/// </summary>
public class NestedOptionsValidatorTests
{
    private static IConfiguration ShippedConfiguration =>
        new ConfigurationBuilder().AddJsonFile("api-appsettings.json").Build();

    [Fact]
    public void DocumentOptions_TheShippedConfiguration_Validates()
    {
        var options = ShippedConfiguration.GetSection(DocumentOptions.SectionName).Get<DocumentOptions>() ?? new DocumentOptions();

        Assert.True(new DocumentOptionsValidator().Validate(options).IsValid);
    }

    [Fact]
    public void SheetOptions_TheShippedConfiguration_Validates()
    {
        var options = ShippedConfiguration.GetSection(SheetOptions.SectionName).Get<SheetOptions>() ?? new SheetOptions();

        Assert.True(new SheetOptionsValidator().Validate(options).IsValid);
    }

    [Fact]
    public void DocumentOptions_ChunkOverlapNotBelowItsSize_IsRejected()
    {
        var options = new DocumentOptions { Chunking = new ChunkingOptions { MaxTokens = 256, OverlapTokens = 256 } };

        var result = new DocumentOptionsValidator().Validate(options);

        Assert.Contains(
            result.Errors,
            e => e.ErrorMessage == "Documents:Chunking:OverlapTokens must be smaller than Documents:Chunking:MaxTokens.");
    }

    [Fact]
    public void DocumentOptions_ChunkSizeOutOfRange_IsRejected()
    {
        var options = new DocumentOptions { Chunking = new ChunkingOptions { MaxTokens = 0, OverlapTokens = 0 } };

        var result = new DocumentOptionsValidator().Validate(options);

        Assert.Contains(result.Errors, e => e.ErrorMessage == "Documents:Chunking:MaxTokens must be between 1 and 8192.");
    }

    [Theory]
    [InlineData(0.05d, 100)]
    [InlineData(0.5d, 0)]
    public void SheetOptions_RowWindowOutOfRange_IsRejected(double targetTokenFraction, int maxRows)
    {
        var options = new SheetOptions
        {
            RowWindow = new RowWindowOptions { TargetTokenFraction = targetTokenFraction, MaxRows = maxRows }
        };

        var result = new SheetOptionsValidator().Validate(options);

        Assert.Contains(result.Errors, e => e.ErrorMessage.StartsWith("Sheets:RowWindow:", StringComparison.Ordinal));
    }
}
