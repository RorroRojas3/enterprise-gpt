using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using System.Collections.Concurrent;

namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// Resolves and caches the tokenizer for a deployment.
/// </summary>
/// <remarks>
/// Building a tokenizer loads and indexes a large vocabulary, so it must not happen per request.
/// Deployments are cached separately from encodings so that several deployments resolving to the
/// same encoding share one loaded vocabulary rather than each paying for its own.
/// </remarks>
/// <param name="logger">Receives the one-time notice for each unrecognized deployment.</param>
public sealed class TiktokenTokenizerCache(ILogger<TiktokenTokenizerCache> logger)
{
    private readonly ILogger<TiktokenTokenizerCache> _logger = logger;
    private readonly ConcurrentDictionary<string, Tokenizer> _byDeployment = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Tokenizer> _byEncoding = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the tokenizer for a deployment, falling back to a named encoding when the deployment
    /// is not a model identifier the library recognizes.
    /// </summary>
    /// <param name="deploymentName">The provider-side deployment name.</param>
    /// <param name="fallbackEncoding">The encoding to use when the deployment is unrecognized.</param>
    /// <returns>The tokenizer to count with.</returns>
    public Tokenizer Get(string? deploymentName, string fallbackEncoding)
    {
        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            return GetForEncoding(fallbackEncoding);
        }

        return _byDeployment.GetOrAdd(deploymentName, name =>
        {
            try
            {
                return TiktokenTokenizer.CreateForModel(name);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                // Deployment names are operator-chosen aliases, so this is the ordinary case rather
                // than a fault. Logged once per deployment because the cache never re-enters here.
                _logger.LogInformation(
                    "Deployment {DeploymentName} is not a recognized tokenizer model; estimating with {FallbackEncoding}.",
                    name,
                    fallbackEncoding);

                return GetForEncoding(fallbackEncoding);
            }
        });
    }

    private Tokenizer GetForEncoding(string encoding) =>
        _byEncoding.GetOrAdd(encoding, name => TiktokenTokenizer.CreateForEncoding(name));
}
