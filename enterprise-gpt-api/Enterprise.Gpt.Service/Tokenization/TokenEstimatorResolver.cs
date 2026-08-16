using System.Collections.Concurrent;
using Enterprise.Gpt.Dto.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// Resolves the token estimator registered for a provider.
/// </summary>
public interface ITokenEstimatorResolver
{
    /// <summary>
    /// Gets the uncalibrated estimator, for text that belongs to no provider yet.
    /// </summary>
    /// <remarks>
    /// Exposed directly so a caller with no provider in hand does not have to look one up that is
    /// designed to miss — which would log a "no estimator registered" warning that reads like a
    /// misconfiguration rather than a deliberate default.
    /// </remarks>
    ITokenEstimator Default { get; }

    /// <summary>
    /// Resolves the estimator for a provider.
    /// </summary>
    /// <param name="providerId">The provider serving the model.</param>
    /// <returns>The provider's estimator, or the uncalibrated default when it has none.</returns>
    /// <remarks>
    /// Never throws for an unknown provider, unlike chat client resolution. A missing chat client
    /// means the turn cannot run at all; a missing estimator only means the count is uncalibrated,
    /// and failing a turn that could otherwise be answered would trade a real capability for an
    /// approximation.
    /// </remarks>
    ITokenEstimator Resolve(Guid providerId);
}

/// <summary>
/// Resolves provider-specific token estimators out of the keyed service registrations, mirroring
/// how chat clients are resolved.
/// </summary>
/// <remarks>
/// Registered as a singleton, so the captured provider is the root container and the estimators it
/// hands back are the keyed singletons themselves — nothing scoped is captured.
/// </remarks>
/// <param name="serviceProvider">The container the keyed estimators are registered in.</param>
/// <param name="fallback">The uncalibrated estimator used for a provider with no registration.</param>
/// <param name="logger">The logger.</param>
public sealed class TokenEstimatorResolver(
    IServiceProvider serviceProvider,
    ITokenEstimator fallback,
    ILogger<TokenEstimatorResolver> logger) : ITokenEstimatorResolver
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ITokenEstimator _fallback = fallback;
    private readonly ILogger<TokenEstimatorResolver> _logger = logger;

    // Logged once per provider rather than per turn: an unregistered provider is a permanent
    // condition, and a warning on every message would bury the rest of the log under it.
    private readonly ConcurrentDictionary<Guid, bool> _reported = new();

    /// <inheritdoc />
    public ITokenEstimator Default => _fallback;

    /// <inheritdoc />
    public ITokenEstimator Resolve(Guid providerId)
    {
        if (Providers.ServiceKeys.TryGetValue(providerId, out var serviceKey))
        {
            var estimator = _serviceProvider.GetKeyedService<ITokenEstimator>(serviceKey);

            if (estimator is not null)
            {
                return estimator;
            }
        }

        if (_reported.TryAdd(providerId, true))
        {
            _logger.LogWarning(
                "No token estimator is registered for provider {ProviderId}; counting with the uncalibrated default. Message token counts for this provider carry the tiktoken error uncorrected.",
                providerId);
        }

        return _fallback;
    }
}
