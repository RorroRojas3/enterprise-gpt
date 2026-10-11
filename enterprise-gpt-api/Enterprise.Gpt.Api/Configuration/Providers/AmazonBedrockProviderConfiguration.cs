using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Andes.Extensions.AI;
using Enterprise.Gpt.Api.Observability;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Validation;
using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Configuration.Providers;

internal static class AmazonBedrockProviderConfiguration
{
    #region Public static methods

    internal static IServiceCollection AddAmazonBedrockProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<AmazonBedrockOptions>, AmazonBedrockOptionsValidator>();
        services.AddOptions<AmazonBedrockOptions>()
            .Bind(configuration.GetSection(AmazonBedrockOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        if (!configuration.GetValue<bool>($"{AmazonBedrockOptions.SectionName}:Enabled"))
        {
            return services;
        }

        // Registered in its own right: the MEAI adapter's Dispose is a deliberate no-op because it does not own
        // the runtime client, so one created inside the chat-client factory would never be disposed.
        services.AddSingleton<IAmazonBedrockRuntime>(services =>
        {
            var settings = services.GetRequiredService<IOptions<AmazonBedrockOptions>>().Value;
            var config = new AmazonBedrockRuntimeConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region),
                // The SDK's default token chain never reads AWS_BEARER_TOKEN_BEDROCK, so the API key is handed
                // over here; pinning the preference stops SigV4 winning on a host with ambient AWS credentials.
                AWSTokenProvider = new StaticTokenProvider(settings.ApiKey, null),
                AuthSchemePreference = ["httpBearerAuth"]
            };

            // Anonymous credentials keep the SigV4 resolver from probing IMDS; the bearer token authorizes.
            return new AmazonBedrockRuntimeClient(new AnonymousAWSCredentials(), config);
        });

        services.AddKeyedChatClient(
                ChatClientKeys.AmazonBedrock,
                services => services.GetRequiredService<IAmazonBedrockRuntime>()
                    .AsIChatClient(services.GetRequiredService<IOptions<AmazonBedrockOptions>>().Value.DefaultModelId))
            .UseEnterpriseGptTelemetry()
            .UseToolTracking(ChatClientPipeline.ConfigureToolTracking)
            .UseFunctionInvocation(null, ChatClientPipeline.ConfigureFunctionInvocation);

        return services;
    }

    #endregion
}
