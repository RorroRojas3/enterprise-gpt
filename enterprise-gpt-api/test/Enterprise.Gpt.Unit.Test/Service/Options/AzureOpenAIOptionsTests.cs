using Enterprise.Gpt.Service.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Options;

public sealed class AzureOpenAIOptionsTests
{
    [Theory]
    [InlineData("https://test.services.ai.azure.com/", "https://test.services.ai.azure.com/openai/v1/")]
    // Azure documents both hosts, and a portal-copied root often lacks the trailing slash, without which
    // a relative path replaces the last segment instead of extending it.
    [InlineData("https://test.services.ai.azure.com", "https://test.services.ai.azure.com/openai/v1/")]
    public void V1Endpoint_FromAResourceRoot_AppendsTheOpenAIV1Path(string url, string expected)
    {
        var options = new AzureOpenAIOptions { Url = url };

        Assert.Equal(expected, options.V1Endpoint.ToString());
    }

    [Theory]
    [InlineData("https://test.services.ai.azure.com/", true)]
    [InlineData("http://localhost:8080/", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("test.services.ai.azure.com", false)]
    [InlineData("ftp://test.services.ai.azure.com/", false)]
    public void IsUrlAbsolute_AcceptsOnlyAnAbsoluteHttpUri(string url, bool expected)
    {
        Assert.Equal(expected, new AzureOpenAIOptions { Url = url }.IsUrlAbsolute);
    }

    [Theory]
    [InlineData("https://test.services.ai.azure.com/", true)]
    // Configuring the v1 endpoint rather than the root would have it appended twice, and the 404
    // that produces names neither the setting nor the cause.
    [InlineData("https://test.services.ai.azure.com/openai/v1/", false)]
    [InlineData("https://test.services.ai.azure.com/OpenAI/V1", false)]
    // The host of a classic Azure OpenAI resource contains "openai"; only the path may not.
    [InlineData("https://test.openai.azure.com/", true)]
    // Half the path repeated is the same 404 as all of it.
    [InlineData("https://test.services.ai.azure.com/openai", false)]
    public void IsUrlResourceRoot_RejectsAUrlThatAlreadyCarriesTheV1Path(string url, bool expected)
    {
        Assert.Equal(expected, new AzureOpenAIOptions { Url = url }.IsUrlResourceRoot);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("none", false)]
    [InlineData("None", false)]
    public void IsReasoningSummaryRequested_TreatsOnlyNoneAsDeclining(string summary, bool expected)
    {
        var options = new AzureOpenAIOptions { ReasoningSummary = summary };

        Assert.Equal(expected, options.IsReasoningSummaryRequested);
    }

    [Theory]
    [InlineData("")]
    // A path, not a URL: Uri accepts it as a file path on Unix but not on Windows, so deriving the
    // endpoint from the Uri constructor shipped file:///openai/v1/ to Linux hosts.
    [InlineData("/openai/v1/")]
    public void V1Endpoint_WithoutAnAbsoluteHttpUrl_ThrowsOnEveryPlatform(string url)
    {
        Assert.Throws<UriFormatException>(() => new AzureOpenAIOptions { Url = url }.V1Endpoint);
    }

    [Fact]
    public void Defaults_AskForAnAutomaticSummaryAtMediumEffort()
    {
        var options = new AzureOpenAIOptions();

        // Cast because FrozenSet satisfies both ISet and IReadOnlySet, which makes the Assert
        // overload ambiguous.
        Assert.Contains(options.ReasoningSummary, (ISet<string>)AzureOpenAIOptions.ReasoningSummaries);
        Assert.Contains(options.ReasoningEffort, (ISet<string>)AzureOpenAIOptions.ReasoningEfforts);
    }
}
