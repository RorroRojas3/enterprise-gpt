using Andes.Extensions.AI;
using Anthropic;
using Enterprise.Gpt.Api.Observability;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Validation;
using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Configuration.Providers;

/// <summary>
/// Registers Anthropic's first-party API.
/// </summary>
internal static class AnthropicProviderConfiguration
{
    /// <summary>
    /// Registers the validated options and, when <c>Anthropic:Enabled</c> is set, the SDK client and the keyed chat
    /// client.
    /// </summary>
    internal static IServiceCollection AddAnthropicProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<AnthropicOptions>, AnthropicOptionsValidator>();
        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        if (!configuration.GetValue<bool>($"{AnthropicOptions.SectionName}:Enabled"))
        {
            return services;
        }

        // A singleton, as the SDK asks: each client carries its own connection and thread pools.
        services.AddSingleton<IAnthropicClient>(services =>
        {
            var settings = services.GetRequiredService<IOptions<AnthropicOptions>>().Value;

            // Assigning ApiKey also suppresses the SDK's environment and profile credential resolution, so an
            // ANTHROPIC_API_KEY on the host cannot authenticate a deployment whose configured key is wrong.
            return new AnthropicClient
            {
                ApiKey = settings.ApiKey,
                Timeout = settings.Timeout,
                MaxRetries = settings.MaxRetries
            };
        });

        services.AddKeyedChatClient(
                ChatClientKeys.Anthropic,
                services =>
                {
                    var settings = services.GetRequiredService<IOptions<AnthropicOptions>>().Value;

                    return services.GetRequiredService<IAnthropicClient>()
                        .AsIChatClient(settings.DefaultModelId, settings.DefaultMaxOutputTokens);
                })
            .UseEnterpriseGptTelemetry()
            .UseToolTracking(ChatClientPipeline.ConfigureToolTracking)
            .UseFunctionInvocation(null, ChatClientPipeline.ConfigureFunctionInvocation)
            // Last, so innermost: ChatClientBuilder applies factories in reverse, and the client closest to the SDK
            // is the one whose ChatOptions the bridge reads.
            .Use((innerClient, services) => new ConfigureOptionsChatClient(
                innerClient,
                AnthropicDefaults.Create(services.GetRequiredService<IOptions<AnthropicOptions>>().Value)));

        return services;
    }
}
