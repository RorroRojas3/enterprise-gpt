using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

/// <summary>
/// Covers the mapping from a model's provider to the keyed chat client that serves it, including the
/// two ways that mapping can come up empty.
/// </summary>
public sealed class ChatClientResolverTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    private ChatClientResolver CreateResolver(params (string Key, IChatClient Client)[] registrations)
    {
        var services = new ServiceCollection();
        foreach (var (key, client) in registrations)
        {
            services.AddKeyedSingleton(key, client);
        }

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new ChatClientResolver(provider);
    }

    [Fact]
    public void Resolve_AzureOpenAIProvider_ReturnsClientRegisteredUnderTheAzureKey()
    {
        var azure = Substitute.For<IChatClient>();
        var bedrock = Substitute.For<IChatClient>();
        var resolver = CreateResolver(
            (ChatClientKeys.AzureOpenAI, azure),
            (ChatClientKeys.AmazonBedrock, bedrock));

        var resolved = resolver.Resolve(Providers.AzureOpenAI);

        Assert.Same(azure, resolved);
    }

    /// <summary>
    /// Azure OpenAI and Azure AI Foundry reach the same resource over the same v1 endpoint and differ
    /// only in API surface, so a slip in <c>Providers.ServiceKeys</c> that collapsed one onto the
    /// other would look entirely plausible — a turn would still stream, just without reasoning, or
    /// with reasoning options a Chat Completions deployment rejects. Asserting each resolves to its
    /// own client and specifically not the other's is what catches that.
    /// </summary>
    [Fact]
    public void Resolve_AzureAIFoundryProvider_ReturnsClientRegisteredUnderTheFoundryKey()
    {
        var azureOpenAI = Substitute.For<IChatClient>();
        var foundry = Substitute.For<IChatClient>();
        var resolver = CreateResolver(
            (ChatClientKeys.AzureOpenAI, azureOpenAI),
            (ChatClientKeys.AzureAIFoundry, foundry));

        var resolved = resolver.Resolve(Providers.AzureAIFoundry);

        Assert.Same(foundry, resolved);
        Assert.NotSame(azureOpenAI, resolved);
    }

    /// <summary>
    /// The shape of a deployment that leaves <c>AzureAIFoundry:Enabled</c> off while a Foundry-backed
    /// model still sits in the catalog. Registering Azure OpenAI here is the point: the sibling
    /// provider on the same endpoint must not silently absorb the turn.
    /// </summary>
    [Fact]
    public void Resolve_AzureAIFoundryProviderWithNoRegisteredClient_ThrowsProviderNotConfiguredException()
    {
        var resolver = CreateResolver((ChatClientKeys.AzureOpenAI, Substitute.For<IChatClient>()));

        var exception = Assert.Throws<ProviderNotConfiguredException>(
            () => resolver.Resolve(Providers.AzureAIFoundry));

        Assert.Equal(Providers.AzureAIFoundry, exception.ProviderId);
    }

    [Fact]
    public void Resolve_AmazonBedrockProvider_ReturnsClientRegisteredUnderTheBedrockKey()
    {
        var azure = Substitute.For<IChatClient>();
        var bedrock = Substitute.For<IChatClient>();
        var resolver = CreateResolver(
            (ChatClientKeys.AzureOpenAI, azure),
            (ChatClientKeys.AmazonBedrock, bedrock));

        var resolved = resolver.Resolve(Providers.AmazonBedrock);

        Assert.Same(bedrock, resolved);
    }

    /// <summary>
    /// Anthropic and Amazon Bedrock both serve Claude, so a copy-paste slip in
    /// <c>Providers.ServiceKeys</c> that pointed Anthropic at the Bedrock key would still produce
    /// plausible-looking answers in every other test. Asserting the client is the Anthropic one and
    /// specifically not the Bedrock one is what catches that.
    /// </summary>
    [Fact]
    public void Resolve_AnthropicProvider_ReturnsClientRegisteredUnderTheAnthropicKey()
    {
        var azure = Substitute.For<IChatClient>();
        var bedrock = Substitute.For<IChatClient>();
        var anthropic = Substitute.For<IChatClient>();
        var resolver = CreateResolver(
            (ChatClientKeys.AzureOpenAI, azure),
            (ChatClientKeys.AmazonBedrock, bedrock),
            (ChatClientKeys.Anthropic, anthropic));

        var resolved = resolver.Resolve(Providers.Anthropic);

        Assert.Same(anthropic, resolved);
        Assert.NotSame(bedrock, resolved);
    }

    /// <summary>
    /// The shape of a deployment that leaves <c>Anthropic:Enabled</c> off while an Anthropic-backed
    /// model still sits in the catalog. Registering Bedrock here is the point: a Claude model must
    /// not silently fall through to the other provider that happens to serve Claude.
    /// </summary>
    [Fact]
    public void Resolve_AnthropicProviderWithNoRegisteredClient_ThrowsProviderNotConfiguredException()
    {
        var resolver = CreateResolver(
            (ChatClientKeys.AzureOpenAI, Substitute.For<IChatClient>()),
            (ChatClientKeys.AmazonBedrock, Substitute.For<IChatClient>()));

        var exception = Assert.Throws<ProviderNotConfiguredException>(
            () => resolver.Resolve(Providers.Anthropic));

        Assert.Equal(Providers.Anthropic, exception.ProviderId);
    }

    /// <summary>
    /// The shape of a deployment that leaves <c>AmazonBedrock:Enabled</c> off while a Bedrock-backed
    /// model still sits in the catalog.
    /// </summary>
    [Fact]
    public void Resolve_ProviderWithNoRegisteredClient_ThrowsProviderNotConfiguredException()
    {
        var resolver = CreateResolver((ChatClientKeys.AzureOpenAI, Substitute.For<IChatClient>()));

        var exception = Assert.Throws<ProviderNotConfiguredException>(
            () => resolver.Resolve(Providers.AmazonBedrock));

        Assert.Equal(Providers.AmazonBedrock, exception.ProviderId);
    }

    /// <summary>
    /// <c>Core.Ref.Provider</c> accepts rows the code knows nothing about, and model creation only
    /// checks that the provider row exists — so an unmapped id has to fail here rather than resolve
    /// to whichever client happens to be registered.
    /// </summary>
    [Fact]
    public void Resolve_UnknownProviderId_ThrowsProviderNotConfiguredException()
    {
        var unknownProviderId = Guid.NewGuid();
        var resolver = CreateResolver(
            (ChatClientKeys.AzureOpenAI, Substitute.For<IChatClient>()),
            (ChatClientKeys.AmazonBedrock, Substitute.For<IChatClient>()));

        var exception = Assert.Throws<ProviderNotConfiguredException>(
            () => resolver.Resolve(unknownProviderId));

        Assert.Equal(unknownProviderId, exception.ProviderId);
    }
}
