using System.Collections.Concurrent;
using Enterprise.Gpt.Common.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Conversations.Tokenization;

/// <summary>
/// Resolves the token estimator registered for a provider.
/// </summary>
public interface ITokenEstimatorResolver
{
    /// <summary>
    /// Gets the uncalibrated estimator, for text that belongs to no provider yet.
    /// </summary>
    /// <remarks>
    /// Saves a caller with no provider a lookup designed to miss, whose warning would read like a
    /// misconfiguration.
    /// </remarks>
    ITokenEstimator Default { get; }

    /// <summary>Resolves a provider's estimator, or the uncalibrated default when it has none.</summary>
    /// <remarks>
    /// Never throws, unlike chat client resolution: a missing estimator only means an uncalibrated count,
    /// which is no reason to fail a turn.
    /// </remarks>
    ITokenEstimator Resolve(Guid providerId);
}

/// <summary>
/// Resolves provider-specific token estimators out of the keyed service registrations, mirroring
/// how chat clients are resolved.
/// </summary>
/// <remarks>
/// A singleton over the root container that hands back keyed singletons, so nothing scoped is captured.
/// </remarks>
public sealed partial class TokenEstimatorResolver(
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
            LogNoTokenEstimator(_logger, providerId);
        }

        return _fallback;
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "No token estimator is registered for provider {ProviderId}; counting with the uncalibrated default. Message token counts for this provider carry the tiktoken error uncorrected.")]
    private static partial void LogNoTokenEstimator(ILogger logger, Guid providerId);

    #endregion
}
