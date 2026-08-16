using System.Collections.Concurrent;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// Resolves and caches the tiktoken vocabulary used to count a deployment's text.
/// </summary>
/// <remarks>
/// <para>
/// A singleton shared by every provider's estimator. Building a tokenizer loads and indexes a large
/// vocabulary, so it must not happen per request — and holding one cache rather than one per
/// provider is what stops the same vocabulary being loaded four times over.
/// </para>
/// <para>
/// Selection mirrors the document chunker: ask the library for the deployment name's own encoding
/// first, and fall back when it does not recognize the name. Azure OpenAI addresses models by
/// deployment name, which is chosen by whoever created the deployment and is usually not a model
/// identifier at all.
/// </para>
/// </remarks>
/// <param name="options">The estimator settings, read for the fallback encoding.</param>
/// <param name="logger">The logger.</param>
public sealed class TokenizerProvider(IOptions<TokenEstimationOptions> options, ILogger<TokenizerProvider> logger)
{
    private readonly ConcurrentDictionary<string, Tokenizer> _byDeployment = new(StringComparer.OrdinalIgnoreCase);
    private readonly TokenEstimationOptions _options = options.Value;
    private readonly ILogger<TokenizerProvider> _logger = logger;

    /// <summary>
    /// Gets the tokenizer for a deployment, building and caching it on first use.
    /// </summary>
    /// <param name="deploymentName">
    /// The provider-side model or deployment identifier, or <see langword="null"/> when unknown.
    /// </param>
    /// <returns>A tokenizer. Never <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when even the configured fallback encoding cannot be built, which means the
    /// vocabulary package for it is not referenced.
    /// </exception>
    public Tokenizer Get(string? deploymentName)
    {
        return _byDeployment.GetOrAdd(deploymentName ?? string.Empty, Build);
    }

    private Tokenizer Build(string deploymentName)
    {
        if (!string.IsNullOrWhiteSpace(deploymentName))
        {
            try
            {
                var tokenizer = TiktokenTokenizer.CreateForModel(deploymentName);

                _logger.LogInformation(
                    "Estimating tokens for '{DeploymentName}' with the tokenizer the library reports for it", deploymentName);

                return tokenizer;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                _logger.LogInformation(
                    "'{DeploymentName}' is not a recognized tokenizer model name (it is likely a deployment alias); falling back to the {Encoding} encoding",
                    deploymentName, _options.FallbackEncoding);
            }
        }

        try
        {
            return TiktokenTokenizer.CreateForEncoding(_options.FallbackEncoding);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Tokenization:FallbackEncoding is '{_options.FallbackEncoding}', which this application cannot build. Reference the matching Microsoft.ML.Tokenizers.Data package or set a supported encoding.", ex);
        }
    }
}
