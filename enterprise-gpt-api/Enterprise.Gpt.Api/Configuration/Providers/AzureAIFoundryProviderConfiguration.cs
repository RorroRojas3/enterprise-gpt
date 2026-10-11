using System.ClientModel;
using Andes.Extensions.AI;
using Enterprise.Gpt.Api.Observability;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Validation;
using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Enterprise.Gpt.Api.Configuration.Providers;

/// <summary>
/// Registers Azure AI Foundry, the Chat Completions route to the same resource Azure OpenAI uses.
/// </summary>
internal static class AzureAIFoundryProviderConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers the validated options and, when <c>AzureAIFoundry:Enabled</c> is set, the keyed chat client.
    /// </summary>
    /// <remarks>
    /// Chat Completions is what lets it serve Foundry Models (DeepSeek, Llama, Mistral) beside Azure OpenAI
    /// deployments, at the cost of reasoning. Its own section, because the two carry different credentials.
    /// </remarks>
    internal static IServiceCollection AddAzureAIFoundryProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<AzureAIFoundryOptions>, AzureAIFoundryOptionsValidator>();
        services.AddOptions<AzureAIFoundryOptions>()
            .Bind(configuration.GetSection(AzureAIFoundryOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        if (!configuration.GetValue<bool>($"{AzureAIFoundryOptions.SectionName}:Enabled"))
        {
            return services;
        }

        services.AddKeyedChatClient(
                ChatClientKeys.AzureAIFoundry,
                services =>
                {
                    var settings = services.GetRequiredService<IOptions<AzureAIFoundryOptions>>().Value;

                    // Both calls are stable, which keeps the experimental surface confined to the Responses provider.
                    return new OpenAIClient(
                            new ApiKeyCredential(settings.ApiKey),
                            new OpenAIClientOptions { Endpoint = settings.V1Endpoint })
                        .GetChatClient(settings.DefaultModel)
                        .AsIChatClient();
                })
            .UseEnterpriseGptTelemetry()
            .UseToolTracking(ChatClientPipeline.ConfigureToolTracking)
            .UseFunctionInvocation(null, ChatClientPipeline.ConfigureFunctionInvocation)
            .Use((innerClient, _) => new ConfigureOptionsChatClient(innerClient, AzureAIFoundryDefaults.Create()));

        return services;
    }

    #endregion
}
