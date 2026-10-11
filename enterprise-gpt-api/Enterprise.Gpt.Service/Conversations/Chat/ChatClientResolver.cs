using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Conversations.Chat.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Enterprise.Gpt.Service.Conversations.Chat;

/// <summary>
/// Selects the chat client that serves a model, based on the model's provider.
/// </summary>
public interface IChatClientResolver
{
    /// <summary>Resolves the <see cref="IChatClient"/> registered for a provider.</summary>
    /// <remarks>
    /// An unknown provider and a client disabled by configuration are deliberately one condition:
    /// telling them apart would only leak deployment detail.
    /// </remarks>
    /// <exception cref="ProviderNotConfiguredException">The provider has no service key or client.</exception>
    IChatClient Resolve(Guid providerId);
}

/// <summary>Resolves provider-specific chat clients from the keyed service registrations.</summary>
/// <remarks>
/// A singleton over the root container that hands back keyed singletons, so nothing scoped is captured.
/// </remarks>
public sealed class ChatClientResolver(IServiceProvider serviceProvider) : IChatClientResolver
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    /// <inheritdoc />
    public IChatClient Resolve(Guid providerId) =>
        Providers.ServiceKeys.TryGetValue(providerId, out var serviceKey)
            ? _serviceProvider.GetKeyedService<IChatClient>(serviceKey)
                ?? throw new ProviderNotConfiguredException(providerId)
            : throw new ProviderNotConfiguredException(providerId);
}
