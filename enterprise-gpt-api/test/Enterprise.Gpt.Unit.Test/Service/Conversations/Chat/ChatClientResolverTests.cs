using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Conversations.Chat;
using Enterprise.Gpt.Service.Conversations.Chat.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Conversations.Chat;

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
    /// Azure OpenAI and Foundry share one v1 endpoint and differ only in API surface, so a
    /// <c>Providers.ServiceKeys</c> slip collapsing one onto the other would still stream plausibly.
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
    /// Anthropic and Bedrock both serve Claude, so a <c>Providers.ServiceKeys</c> slip pointing one at the
    /// other would still answer plausibly.
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
    /// Model creation only checks the provider row exists, so an unmapped id must fail here rather than
    /// resolve to whichever client is registered.
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
