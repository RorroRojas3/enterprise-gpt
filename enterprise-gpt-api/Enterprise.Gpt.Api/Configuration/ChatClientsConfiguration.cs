using Andes.Extensions.AI;
using Enterprise.Gpt.Api.Configuration.Providers;
using Enterprise.Gpt.Service.Conversations.Chat;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers every chat provider and the resolver that picks one per model.
/// </summary>
internal static class ChatClientsConfiguration
{
    /// <summary>
    /// Registers the usage observer, the four providers under their <c>ChatClientKeys</c>, and the resolver.
    /// </summary>
    internal static IServiceCollection AddEnterpriseGptChatClients(this IServiceCollection services, IConfiguration configuration)
    {
        // Before the clients: UseToolTracking collects every IChatProgressObserver when a client is built, and
        // this one is how a completed request's usage report reaches the code that started it, including for a
        // turn the user cancelled.
        services.AddSingleton<IChatProgressObserver, ChatUsageObserver>();

        services.AddAzureOpenAIProvider(configuration);
        services.AddAzureAIFoundryProvider(configuration);
        services.AddAmazonBedrockProvider(configuration);
        services.AddAnthropicProvider(configuration);

        // Turns a model's ProviderId into one of the keyed clients above at request time.
        services.AddSingleton<IChatClientResolver, ChatClientResolver>();

        return services;
    }
}
