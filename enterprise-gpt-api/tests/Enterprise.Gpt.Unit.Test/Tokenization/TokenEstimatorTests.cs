using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tokenization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tokenization;

public sealed class TokenEstimatorTests
{
    private static readonly Guid ProviderId = Guid.Parse("3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0");

    private static TiktokenTokenEstimator CreateEstimator(
        double? calibration = null, string fallbackEncoding = "cl100k_base")
    {
        var settings = new TokenEstimationOptions { FallbackEncoding = fallbackEncoding };

        if (calibration is { } multiplier)
        {
            settings.ProviderCalibration[ProviderId.ToString()] = multiplier;
        }

        var options = Options.Create(settings);

        return new TiktokenTokenEstimator(
            ProviderId, new TokenizerProvider(options, NullLogger<TokenizerProvider>.Instance), options);
    }

    /// <summary>
    /// The vocabulary the modern OpenAI families use. The <c>Cl100kBase</c> package alone cannot
    /// count them, so this pins that the data package is actually referenced rather than merely
    /// requested.
    /// </summary>
    [Fact]
    public void CreateForEncoding_O200kBase_IsAvailable()
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        Assert.NotNull(tokenizer);
        Assert.True(tokenizer.CountTokens("hello world") > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CountTokens_EmptyText_ReturnsZero(string? text)
    {
        Assert.Equal(0, CreateEstimator().CountTokens(text, "gpt-4o"));
    }

    [Fact]
    public void CountTokens_NonEmptyText_ReturnsAPositiveCount()
    {
        Assert.True(CreateEstimator().CountTokens("The quick brown fox jumps over the lazy dog.", "gpt-4o") > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("my-gpt-deployment")]
    [InlineData(null)]
    public void CountTokens_UnrecognizedDeploymentName_FallsBackRatherThanThrowing(string? deploymentName)
    {
        // Azure OpenAI addresses models by deployment name, which is rarely a name the tokenizer
        // library recognizes; falling back must not fail the turn that is already streaming.
        Assert.True(CreateEstimator().CountTokens("hello world", deploymentName) > 0);
    }

    [Fact]
    public void CountTokens_NoCalibrationConfigured_ReturnsTheRawCount()
    {
        var raw = TiktokenTokenizer.CreateForEncoding("cl100k_base").CountTokens("hello world");

        Assert.Equal(raw, CreateEstimator().CountTokens("hello world", "unknown-deployment"));
    }

    [Fact]
    public void CountTokens_CalibrationConfigured_ScalesTheRawCount()
    {
        var raw = CreateEstimator().CountTokens("The quick brown fox jumps over the lazy dog.", "unknown-deployment");

        var calibrated = CreateEstimator(calibration: 1.2)
            .CountTokens("The quick brown fox jumps over the lazy dog.", "unknown-deployment");

        Assert.Equal((long)Math.Ceiling(raw * 1.2), calibrated);
        Assert.True(calibrated > raw);
    }

    [Fact]
    public void CountTokens_CalibrationConfigured_RoundsUp()
    {
        // The budget this feeds is a ceiling, so rounding down is the direction that overflows a
        // context window.
        var raw = CreateEstimator().CountTokens("hello", "unknown-deployment");

        var calibrated = CreateEstimator(calibration: 1.01).CountTokens("hello", "unknown-deployment");

        Assert.True(calibrated >= raw);
    }

    [Fact]
    public void CountTokens_FallbackEncodingCannotBeBuilt_ThrowsNamingTheSetting()
    {
        var estimator = CreateEstimator(fallbackEncoding: "not-a-real-encoding");

        var exception = Assert.Throws<InvalidOperationException>(
            () => estimator.CountTokens("hello", "unknown-deployment"));

        Assert.Contains("Tokenization:FallbackEncoding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CountTokens_SameTextTwice_IsDeterministic()
    {
        var estimator = CreateEstimator();

        Assert.Equal(
            estimator.CountTokens("a repeatable sentence", "gpt-4o"),
            estimator.CountTokens("a repeatable sentence", "gpt-4o"));
    }
}
