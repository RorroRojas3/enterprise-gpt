using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Service.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Options;

/// <remarks>
/// The URL predicates are covered by <see cref="Service.Options.AzureOpenAIOptionsTests"/> on the same
/// <see cref="AzureV1EndpointOptions"/> base; these pin that the derivation is wired at all.
/// </remarks>
public sealed class AzureAIFoundryOptionsTests
{
    [Fact]
    public void V1Endpoint_FromAResourceRoot_AppendsTheOpenAIV1Path()
    {
        var options = new AzureAIFoundryOptions { Url = "https://test.services.ai.azure.com" };

        Assert.Equal("https://test.services.ai.azure.com/openai/v1/", options.V1Endpoint.ToString());
    }

    /// <summary>
    /// Off by default is what lets an existing deployment upgrade without adding a section, and what
    /// makes a model row on this provider fail with a 503 rather than resolving to nothing.
    /// </summary>
    [Fact]
    public void Enabled_DefaultsToOff()
    {
        Assert.False(new AzureAIFoundryOptions().Enabled);
    }

    /// <summary>
    /// Reasoning is a Responses-API feature; the absence is the contract <c>AzureAIFoundryDefaults</c>
    /// enforces at request time.
    /// </summary>
    [Fact]
    public void Options_CarryNoReasoningSettings()
    {
        var names = typeof(AzureAIFoundryOptions)
            .GetProperties()
            .Select(property => property.Name);

        Assert.DoesNotContain(names, name => name.Contains("Reasoning", StringComparison.Ordinal));
    }
}
