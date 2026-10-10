using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Enterprise.Gpt.Service.Chat;

/// <summary>
/// Selects the chat client that serves a model, based on the model's provider.
/// </summary>
public interface IChatClientResolver
{
    /// <summary>
    /// Resolves the <see cref="IChatClient"/> registered for a provider.
    /// </summary>
    /// <param name="providerId">The <c>Core.Ref.Provider</c> identifier carried by the model.</param>
    /// <returns>The chat client registered under that provider's service key.</returns>
    /// <remarks>
    /// A provider with no service key and a provider whose client is switched off by configuration
    /// are one condition to a caller: this deployment cannot serve the model. Telling them apart in
    /// the response would only leak deployment detail the caller can do nothing about.
    /// </remarks>
    /// <exception cref="ProviderNotConfiguredException">
    /// The provider has no service key, or its client is not registered in this deployment.
    /// </exception>
    IChatClient Resolve(Guid providerId);
}

/// <summary>
/// Resolves provider-specific chat clients out of the keyed service registrations.
/// </summary>
/// <remarks>
/// Registered as a singleton, so the captured provider is the root container and the clients it
/// hands back are the keyed singletons themselves — nothing scoped is captured.
/// </remarks>
/// <param name="serviceProvider">The container the keyed chat clients are registered in.</param>
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
