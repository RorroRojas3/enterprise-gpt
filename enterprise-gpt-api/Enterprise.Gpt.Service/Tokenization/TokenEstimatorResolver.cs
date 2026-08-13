using Enterprise.Gpt.Dto.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// Selects the token estimator registered for a provider.
/// </summary>
public interface ITokenEstimatorResolver
{
    /// <summary>
    /// Resolves the estimator for a provider.
    /// </summary>
    /// <param name="providerId">The provider that will serve the turn.</param>
    /// <returns>The provider's estimator, or the default one when it has none.</returns>
    ITokenEstimator Resolve(Guid providerId);
}

/// <summary>
/// Resolves a keyed estimator from the same provider registry the chat clients use.
/// </summary>
/// <remarks>
/// Deliberately falls back rather than throwing, which is where this parts company with
/// <see cref="Chat.IChatClientResolver"/>: a turn without a chat client cannot run at all, but a
/// turn without an estimator can — it just records a less exact number. Failing the turn would
/// trade a small inaccuracy for a total outage.
/// </remarks>
/// <param name="serviceProvider">Supplies the keyed estimators and the unkeyed default.</param>
/// <param name="logger">Receives the one-time notice for each provider that falls back.</param>
public sealed class TokenEstimatorResolver(
    IServiceProvider serviceProvider,
    ILogger<TokenEstimatorResolver> logger) : ITokenEstimatorResolver
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<TokenEstimatorResolver> _logger = logger;
    private readonly ConcurrentDictionary<Guid, byte> _warnedProviders = new();

    /// <inheritdoc />
    public ITokenEstimator Resolve(Guid providerId)
    {
        if (Providers.ServiceKeys.TryGetValue(providerId, out var serviceKey))
        {
            var keyed = _serviceProvider.GetKeyedService<ITokenEstimator>(serviceKey);
            if (keyed is not null)
            {
                return keyed;
            }
        }

        if (_warnedProviders.TryAdd(providerId, 0))
        {
            _logger.LogWarning(
                "No token estimator is registered for provider {ProviderId}; estimating with the default tokenizer.",
                providerId);
        }

        return _serviceProvider.GetRequiredService<ITokenEstimator>();
    }
}
