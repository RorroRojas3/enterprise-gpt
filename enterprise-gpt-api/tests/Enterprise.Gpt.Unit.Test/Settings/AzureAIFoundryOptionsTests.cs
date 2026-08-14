using Enterprise.Gpt.Service.Settings;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Settings;

/// <summary>
/// Covers the predicates <c>Program.cs</c> wires as startup validators, and the endpoint the chat
/// client is built against.
/// </summary>
public sealed class AzureAIFoundryOptionsTests
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
        var options = new AzureAIFoundryOptions { Url = url };

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
        Assert.Equal(expected, new AzureAIFoundryOptions { Url = url }.IsUrlAbsolute);
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
        Assert.Equal(expected, new AzureAIFoundryOptions { Url = url }.IsUrlResourceRoot);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("detailed", true)]
    [InlineData("none", false)]
    [InlineData("None", false)]
    public void IsReasoningSummaryRequested_TreatsOnlyNoneAsDeclining(string summary, bool expected)
    {
        var options = new AzureAIFoundryOptions { ReasoningSummary = summary };

        Assert.Equal(expected, options.IsReasoningSummaryRequested);
    }

    [Fact]
    public void V1Endpoint_WithNoUrl_ThrowsRatherThanBuildingANonsenseEndpoint()
    {
        // The validator rejects this first; the throw is what stops a caller that skipped it from
        // getting a client pointed at nowhere.
        Assert.Throws<UriFormatException>(() => new AzureAIFoundryOptions { Url = "" }.V1Endpoint);
    }

    [Fact]
    public void Defaults_AskForAnAutomaticSummaryAtMediumEffort()
    {
        var options = new AzureAIFoundryOptions();

        // Cast because FrozenSet satisfies both ISet and IReadOnlySet, which makes the Assert
        // overload ambiguous.
        Assert.Contains(options.ReasoningSummary, (ISet<string>)AzureAIFoundryOptions.ReasoningSummaries);
        Assert.Contains(options.ReasoningEffort, (ISet<string>)AzureAIFoundryOptions.ReasoningEfforts);
    }
}
