using Andes.Extensions.AI;
using Enterprise.Gpt.Api.Configuration.Providers;
using Enterprise.Gpt.Service.Conversations.Chat;

namespace Enterprise.Gpt.Api.Configuration;

internal static class ChatClientsConfiguration
{
    #region Public static methods

    internal static IServiceCollection AddEnterpriseGptChatClients(this IServiceCollection services, IConfiguration configuration)
    {
        // Before the clients: UseToolTracking collects every IChatProgressObserver when a client is built,
        // and this one carries a request's usage back to its caller, cancelled turns included.
        services.AddSingleton<IChatProgressObserver, ChatUsageObserver>();

        services.AddAzureOpenAIProvider(configuration);
        services.AddAzureAIFoundryProvider(configuration);
        services.AddAmazonBedrockProvider(configuration);
        services.AddAnthropicProvider(configuration);

        services.AddSingleton<IChatClientResolver, ChatClientResolver>();

        return services;
    }

    #endregion
}
