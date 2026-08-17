using Enterprise.Gpt.Service.Settings;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Settings;

/// <summary>
/// Covers the predicates <c>Program.cs</c> wires as startup validators, and the endpoint the chat
/// client is built against.
/// </summary>
public sealed class AzureOpenAIOptionsTests
{
    [Theory]
    [InlineData("https://test.services.ai.azure.com/", "https://test.services.ai.azure.com/openai/v1/")]
    [InlineData("https://test.openai.azure.com/", "https://test.openai.azure.com/openai/v1/")]
    // Azure documents both hosts, and a resource root copied out of the portal often lacks the
    // trailing slash — without one, resolving a relative path replaces the last segment instead of
    // extending it, which is the difference between /openai/v1/ and /.
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
    [InlineData("detailed", true)]
    [InlineData("none", false)]
    [InlineData("None", false)]
    public void IsReasoningSummaryRequested_TreatsOnlyNoneAsDeclining(string summary, bool expected)
    {
        var options = new AzureOpenAIOptions { ReasoningSummary = summary };

        Assert.Equal(expected, options.IsReasoningSummaryRequested);
    }

    [Fact]
    public void V1Endpoint_WithNoUrl_ThrowsRatherThanBuildingANonsenseEndpoint()
    {
        // The validator rejects this first; the throw is what stops a caller that skipped it from
        // getting a client pointed at nowhere.
        Assert.Throws<UriFormatException>(() => new AzureOpenAIOptions { Url = "" }.V1Endpoint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("test.services.ai.azure.com")]
    [InlineData("ftp://test.services.ai.azure.com/")]
    // A path, not a URL: Uri accepts this as an implicit file path on Unix but not on Windows, so
    // deriving the endpoint straight from the Uri constructor passed here and shipped file:///openai/v1/
    // to the Linux hosts. The theory runs the same on both.
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
