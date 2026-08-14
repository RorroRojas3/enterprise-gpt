using Enterprise.Gpt.Service.Settings;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Settings;

/// <summary>
/// Covers what the Azure AI Foundry section adds over the shared v1-endpoint behaviour, and pins
/// that it actually inherits that behaviour.
/// </summary>
/// <remarks>
/// The URL predicates themselves are exercised in depth by <see cref="AzureOpenAIOptionsTests"/>,
/// against the same <see cref="AzureV1EndpointOptions"/> base. Repeating every theory here would
/// duplicate the table without testing anything new — what these cover is the derivation being
/// wired at all, which a refactor that gave this type its own copy of the logic would break.
/// </remarks>
public sealed class AzureAIFoundryOptionsTests
{
    [Fact]
    public void V1Endpoint_FromAResourceRoot_AppendsTheOpenAIV1Path()
    {
        var options = new AzureAIFoundryOptions { Url = "https://test.services.ai.azure.com" };

        Assert.Equal("https://test.services.ai.azure.com/openai/v1/", options.V1Endpoint.ToString());
    }

    [Fact]
    public void UrlPredicates_AreInheritedFromTheSharedBase()
    {
        Assert.True(new AzureAIFoundryOptions { Url = "https://test.openai.azure.com/" }.IsUrlAbsolute);
        Assert.False(new AzureAIFoundryOptions { Url = "test.services.ai.azure.com" }.IsUrlAbsolute);
        Assert.False(new AzureAIFoundryOptions { Url = "https://test.services.ai.azure.com/openai/v1/" }.IsUrlResourceRoot);
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
    /// Reasoning is a Responses-API feature, so this provider carries no reasoning settings at all —
    /// the absence is the contract, and <c>AzureAIFoundryChatDefaults</c> enforces it at request
    /// time. A property added here later would be a signal that the enforcement moved.
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
