using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Conversations;
using Enterprise.Gpt.Service.Conversations.Rendering;
using Enterprise.Gpt.Service.Conversations.Tokenization;
using Enterprise.Gpt.Service.Options;
using FluentValidation;
using Microsoft.Extensions.Options;
using ProviderCatalog = Enterprise.Gpt.Common.Constants.Providers;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers conversations: the turn service, its lock, markdown rendering and token estimation.
/// </summary>
internal static class ConversationsConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers the conversation services and the per-provider token estimators.
    /// </summary>
    internal static IServiceCollection AddConversations(this IServiceCollection services, IConfiguration configuration)
    {
        // A negative overhead term or a nonsensical calibration multiplier silently corrupts every stored token
        // count and, through the context budget built on them, shrinks what is replayed to the model.
        services.AddSingleton<IValidator<TokenEstimationOptions>, TokenEstimationOptionsValidator>();
        services.AddOptions<TokenEstimationOptions>()
            .Bind(configuration.GetSection(TokenEstimationOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        services.AddSingleton<TokenizerProvider>();
        services.AddSingleton<PromptOverheadCalculator>();

        // One estimator per provider, keyed exactly as the chat clients are, so a provider's calibration resolves
        // the same way its client does. The vocabularies are shared through TokenizerProvider.
        foreach (var (providerId, serviceKey) in ProviderCatalog.ServiceKeys)
        {
            var provider = providerId;
            services.AddKeyedSingleton<ITokenEstimator>(serviceKey, (services, _) =>
                new TiktokenTokenEstimator(
                    provider,
                    services.GetRequiredService<TokenizerProvider>(),
                    services.GetRequiredService<IOptions<TokenEstimationOptions>>()));
        }

        // The uncalibrated fallback the resolver hands back for a provider with no registration.
        services.AddSingleton<ITokenEstimator>(services =>
            new TiktokenTokenEstimator(
                Guid.Empty,
                services.GetRequiredService<TokenizerProvider>(),
                services.GetRequiredService<IOptions<TokenEstimationOptions>>()));
        services.AddSingleton<ITokenEstimatorResolver, TokenEstimatorResolver>();

        // Rendered once and stored beside each message, so an export is a read rather than a re-render.
        services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();

        services.AddSingleton<IConversationLockService, ConversationLockService>();
        services.AddScoped<IConversationService, ConversationService>();

        return services;
    }

    #endregion
}
