using System.ClientModel;
using Andes.Extensions.AI;
using Enterprise.Gpt.Api.Observability;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Options;
using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Enterprise.Gpt.Api.Configuration.Providers;

/// <summary>
/// Registers Azure OpenAI: the default chat model, the File Agent's client and the embedding generator.
/// </summary>
internal static class AzureOpenAIProviderConfiguration
{
    #region Private methods

    private static IChatClient CreateResponsesClient(IServiceProvider services)
    {
        var settings = services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;

        // The Responses adapter is still experimental; suppressed at the call site rather than project-wide so a
        // second experimental API cannot slip in unnoticed behind it. See AzureOpenAIDefaults for why no upgrade
        // retires either.
#pragma warning disable OPENAI001, MEAI001
        return services.GetRequiredService<OpenAIClient>()
            .GetResponsesClient()
            .AsIChatClient(settings.DefaultModel);
#pragma warning restore OPENAI001, MEAI001
    }

    #endregion

    #region Public static methods

    /// <summary>
    /// Registers the validated options, the shared <see cref="OpenAIClient"/>, both keyed chat clients and the
    /// embedding generator.
    /// </summary>
    /// <remarks>
    /// No <c>Enabled</c> flag: this provider serves the default model and owns the embedding generator, so missing
    /// settings are a broken deployment rather than an opted-out one.
    /// </remarks>
    internal static IServiceCollection AddAzureOpenAIProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<AzureOpenAIOptions>, AzureOpenAIOptionsValidator>();
        services.AddOptions<AzureOpenAIOptions>()
            .Bind(configuration.GetSection(AzureOpenAIOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        // One client for chat, embeddings and the Files API the File Agent uploads through. The hosted-file
        // client is built from it because GetFileClient() cannot see a file the code interpreter wrote into its
        // container. Factories, so new Uri(...) runs after ValidateOnStart and a blank Url fails with the
        // validator's message rather than a bare UriFormatException.
#pragma warning disable OPENAI001, MEAI001
        services.AddSingleton(services =>
        {
            var settings = services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;

            return new OpenAIClient(
                new ApiKeyCredential(settings.ApiKey),
                new OpenAIClientOptions { Endpoint = settings.V1Endpoint });
        });

        services.AddSingleton(services => services.GetRequiredService<OpenAIClient>().AsIHostedFileClient());
#pragma warning restore OPENAI001, MEAI001

        // The Responses API rather than Chat Completions: reasoning summaries exist only on that surface.
        services.AddKeyedChatClient(ChatClientKeys.AzureOpenAI, CreateResponsesClient)
            .UseEnterpriseGptTelemetry()
            // Must precede UseFunctionInvocation: the tracker wraps the tools before that client runs them.
            .UseToolTracking(ChatClientPipeline.ConfigureToolTracking)
            .UseFunctionInvocation(null, ChatClientPipeline.ConfigureFunctionInvocation)
            // Last, so innermost: its ChatOptions are the ones the SDK bridge reads.
            .Use((innerClient, services) => new ConfigureOptionsChatClient(
                innerClient,
                AzureOpenAIDefaults.Create(services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value)));

        // The File Agent's own client over the same route. Its own iteration bound, because the agent spends
        // iterations a turn does not and MaximumIterationsPerRequest has no per-request override. No tool
        // tracking: a nested tracker opens a root scope with no writer and would swallow the run's progress.
        services.AddKeyedChatClient(ChatClientKeys.FileAgent, CreateResponsesClient)
            .UseEnterpriseGptTelemetry()
            .Use((innerClient, services) =>
            {
                var fileAgent = services.GetRequiredService<IOptions<FileAgentOptions>>().Value;

                return new FunctionInvokingChatClient(innerClient, services.GetService<ILoggerFactory>(), services)
                {
                    // Load-bearing here: the agent shares one HostedCodeInterpreterTool across a turn's calls.
                    AllowConcurrentInvocation = false,
                    IncludeDetailedErrors = true,
                    MaximumIterationsPerRequest = fileAgent.MaxIterationsPerRun,
                    MaximumConsecutiveErrorsPerRequest = 5
                };
            })
            .Use((innerClient, services) => new ConfigureOptionsChatClient(
                innerClient,
                AzureOpenAIDefaults.Create(services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value)));

        // The deployment named by EmbeddingModel determines the stored vectors, not the route, so the v1 route
        // reaches the same deployment the legacy one did.
        services.AddEmbeddingGenerator(services =>
        {
            var settings = services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;

            return services.GetRequiredService<OpenAIClient>()
                .GetEmbeddingClient(settings.EmbeddingModel)
                .AsIEmbeddingGenerator();
        });

        return services;
    }

    #endregion
}
