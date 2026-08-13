using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tokenization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tokenization;

public class TokenEstimatorTests
{
    private static TiktokenTokenizerCache CreateCache() => new(NullLogger<TiktokenTokenizerCache>.Instance);

    private static TiktokenTokenEstimator CreateEstimator(Guid providerId, TokenEstimationOptions? options = null) =>
        new(providerId, CreateCache(), Options.Create(options ?? new TokenEstimationOptions()));

    /// <summary>
    /// The modern OpenAI chat families use o200k_base, which the cl100k data package alone cannot
    /// supply — this is what the added vocabulary buys.
    /// </summary>
    [Fact]
    public void CreateForEncoding_O200kBase_ResolvesRatherThanThrowingForAMissingDataFile()
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
        var estimator = CreateEstimator(Providers.AzureOpenAI);

        Assert.Equal(0, estimator.CountTokens("gpt-4o", text));
    }

    [Fact]
    public void CountTokens_SameTextTwice_IsDeterministic()
    {
        var estimator = CreateEstimator(Providers.AzureOpenAI);

        var first = estimator.CountTokens("gpt-4o", "The quick brown fox jumps over the lazy dog.");
        var second = estimator.CountTokens("gpt-4o", "The quick brown fox jumps over the lazy dog.");

        Assert.Equal(first, second);
        Assert.True(first > 0);
    }

    /// <summary>
    /// Deployment names are operator-chosen aliases, so an unrecognized one has to count with the
    /// fallback vocabulary rather than fail the turn that asked for it.
    /// </summary>
    [Fact]
    public void CountTokens_UnrecognizedDeployment_CountsWithTheFallbackEncoding()
    {
        var estimator = CreateEstimator(Providers.AzureOpenAI);

        var count = estimator.CountTokens("our-internal-alias-42", "The quick brown fox.");

        Assert.True(count > 0);
    }

    [Fact]
    public void CountTokens_CalibrationMultiplierConfigured_ScalesTheRawCountUpwards()
    {
        var raw = CreateEstimator(Providers.Anthropic).CountTokens("gpt-4o", "The quick brown fox jumps over the lazy dog.");
        var calibrated = CreateEstimator(Providers.Anthropic, new TokenEstimationOptions
        {
            CalibrationMultipliers = { [Providers.Anthropic] = 1.2 }
        }).CountTokens("gpt-4o", "The quick brown fox jumps over the lazy dog.");

        Assert.Equal((int)Math.Ceiling(raw * 1.2), calibrated);
    }

    /// <summary>
    /// A multiplier configured for one provider must not leak into another's estimates.
    /// </summary>
    [Fact]
    public void CountTokens_MultiplierForAnotherProvider_LeavesThisProvidersCountRaw()
    {
        var options = new TokenEstimationOptions
        {
            CalibrationMultipliers = { [Providers.Anthropic] = 2.0 }
        };

        var raw = CreateEstimator(Providers.AzureOpenAI).CountTokens("gpt-4o", "Hello there.");
        var withOtherProvidersMultiplier = CreateEstimator(Providers.AzureOpenAI, options).CountTokens("gpt-4o", "Hello there.");

        Assert.Equal(raw, withOtherProvidersMultiplier);
    }

    [Fact]
    public void Accuracy_TiktokenEstimator_ReportsEstimated()
    {
        Assert.Equal(TokenAccuracies.Estimated, CreateEstimator(Providers.AzureOpenAI).Accuracy);
    }

    /// <summary>
    /// A provider without its own estimator falls back rather than throwing: a turn can survive a
    /// less exact token count, where a missing chat client would end it.
    /// </summary>
    [Fact]
    public void Resolve_ProviderWithoutAnEstimator_ReturnsTheDefaultInsteadOfThrowing()
    {
        var resolver = CreateResolver();

        var estimator = resolver.Resolve(Guid.NewGuid());

        Assert.NotNull(estimator);
    }

    [Fact]
    public void Resolve_KnownProvider_ReturnsTheKeyedEstimator()
    {
        var resolver = CreateResolver();

        var estimator = resolver.Resolve(Providers.AzureOpenAI);

        Assert.NotNull(estimator);
    }

    private static TokenEstimatorResolver CreateResolver()
    {
        var services = new ServiceCollection()
            .AddSingleton(CreateCache())
            .AddSingleton(Options.Create(new TokenEstimationOptions()));

        services.AddKeyedSingleton<ITokenEstimator>(ChatClientKeys.AzureAIFoundry, (sp, _) =>
            new TiktokenTokenEstimator(Providers.AzureOpenAI, sp.GetRequiredService<TiktokenTokenizerCache>(), sp.GetRequiredService<IOptions<TokenEstimationOptions>>()));
        services.AddSingleton<ITokenEstimator>(sp =>
            new TiktokenTokenEstimator(Guid.Empty, sp.GetRequiredService<TiktokenTokenizerCache>(), sp.GetRequiredService<IOptions<TokenEstimationOptions>>()));

        return new TokenEstimatorResolver(services.BuildServiceProvider(), NullLogger<TokenEstimatorResolver>.Instance);
    }

    private static PromptEstimator CreatePromptEstimator(TokenEstimationOptions? options = null) =>
        new(CreateResolver(), Options.Create(options ?? new TokenEstimationOptions()));

    private static ModelDto CreateModel() => new()
    {
        Id = Guid.NewGuid(),
        ProviderId = Providers.AzureOpenAI,
        Name = "GPT Test",
        DeploymentName = "gpt-4o",
        Description = "A test model."
    };

    [Fact]
    public void EstimateMessages_StoredCountPresent_UsesItRatherThanRecounting()
    {
        var estimator = CreatePromptEstimator();

        var counts = estimator.EstimateMessages(CreateModel(), [("Some content that would count as several tokens.", 4_242)]);

        Assert.Equal(4_242, Assert.Single(counts));
    }

    /// <summary>
    /// A stored zero on non-empty content means the message predates estimation, so it is counted
    /// on the fly rather than treated as free.
    /// </summary>
    [Fact]
    public void EstimateMessages_StoredCountAbsent_CountsTheContentOnTheFly()
    {
        var estimator = CreatePromptEstimator();

        var counts = estimator.EstimateMessages(CreateModel(), [("The quick brown fox jumps over the lazy dog.", 0)]);

        Assert.True(Assert.Single(counts) > 0);
    }

    [Fact]
    public void EstimateOverhead_ToolsAttached_ChargesThePerToolSchemaCost()
    {
        var options = new TokenEstimationOptions { PerToolSchemaTokens = 350, PerMessageFramingTokens = 0 };
        var estimator = CreatePromptEstimator(options);

        var overhead = estimator.EstimateOverhead(CreateModel(), toolCount: 3, instructions: null, messageCount: 0);

        Assert.Equal(1_050, overhead);
    }

    [Fact]
    public void EstimateOverhead_TenMessages_AppliesTheFramingConstantOncePerMessage()
    {
        var options = new TokenEstimationOptions { PerToolSchemaTokens = 0, PerMessageFramingTokens = 4 };
        var estimator = CreatePromptEstimator(options);

        var overhead = estimator.EstimateOverhead(CreateModel(), toolCount: 0, instructions: null, messageCount: 10);

        Assert.Equal(40, overhead);
    }

    /// <summary>
    /// Project instructions ride on the request rather than as a message, so they are billed but
    /// belong to no message — which is exactly what the overhead term exists to hold.
    /// </summary>
    [Fact]
    public void EstimateOverhead_InstructionsSupplied_CountsThemIntoTheOverhead()
    {
        var options = new TokenEstimationOptions { PerToolSchemaTokens = 0, PerMessageFramingTokens = 0 };
        var estimator = CreatePromptEstimator(options);

        var overhead = estimator.EstimateOverhead(CreateModel(), toolCount: 0, instructions: "Always answer in French.", messageCount: 0);

        Assert.True(overhead > 0);
    }

    [Fact]
    public void EstimateOverhead_NoToolsAndNoInstructions_ContributesOnlyTheFramingConstants()
    {
        var options = new TokenEstimationOptions { PerToolSchemaTokens = 350, PerMessageFramingTokens = 4 };
        var estimator = CreatePromptEstimator(options);

        var overhead = estimator.EstimateOverhead(CreateModel(), toolCount: 0, instructions: null, messageCount: 5);

        Assert.Equal(20, overhead);
    }
}
