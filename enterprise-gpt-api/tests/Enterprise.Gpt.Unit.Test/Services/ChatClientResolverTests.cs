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
            (ChatClientKeys.AzureAIFoundry, azure),
            (ChatClientKeys.AmazonBedrock, bedrock));

        var resolved = resolver.Resolve(Providers.AzureOpenAI);

        Assert.Same(azure, resolved);
    }

    [Fact]
    public void Resolve_AmazonBedrockProvider_ReturnsClientRegisteredUnderTheBedrockKey()
    {
        var azure = Substitute.For<IChatClient>();
        var bedrock = Substitute.For<IChatClient>();
        var resolver = CreateResolver(
            (ChatClientKeys.AzureAIFoundry, azure),
            (ChatClientKeys.AmazonBedrock, bedrock));

        var resolved = resolver.Resolve(Providers.AmazonBedrock);

        Assert.Same(bedrock, resolved);
    }

    /// <summary>
    /// The shape of a deployment that leaves <c>AmazonBedrock:Enabled</c> off while a Bedrock-backed
    /// model still sits in the catalog.
    /// </summary>
    [Fact]
    public void Resolve_ProviderWithNoRegisteredClient_ThrowsProviderNotConfiguredException()
    {
        var resolver = CreateResolver((ChatClientKeys.AzureAIFoundry, Substitute.For<IChatClient>()));

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
            (ChatClientKeys.AzureAIFoundry, Substitute.For<IChatClient>()),
            (ChatClientKeys.AmazonBedrock, Substitute.For<IChatClient>()));

        var exception = Assert.Throws<ProviderNotConfiguredException>(
            () => resolver.Resolve(unknownProviderId));

        Assert.Equal(unknownProviderId, exception.ProviderId);
    }
}
