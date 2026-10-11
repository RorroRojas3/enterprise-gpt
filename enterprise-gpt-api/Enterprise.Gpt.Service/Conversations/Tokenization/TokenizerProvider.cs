using System.Collections.Concurrent;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace Enterprise.Gpt.Service.Conversations.Tokenization;

/// <summary>
/// Resolves and caches the tiktoken vocabulary used to count a deployment's text.
/// </summary>
/// <remarks>
/// One shared singleton because building a tokenizer loads a large vocabulary. Like the document chunker,
/// it falls back from the deployment name's encoding because Azure deployment names are rarely model ids.
/// </remarks>
public sealed partial class TokenizerProvider(IOptions<TokenEstimationOptions> options, ILogger<TokenizerProvider> logger)
{
    private readonly ConcurrentDictionary<string, Tokenizer> _byDeployment = new(StringComparer.OrdinalIgnoreCase);
    private readonly TokenEstimationOptions _options = options.Value;
    private readonly ILogger<TokenizerProvider> _logger = logger;

    /// <summary>
    /// Gets the tokenizer for a deployment, building and caching it on first use.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The fallback encoding cannot be built because its vocabulary package is not referenced.
    /// </exception>
    public Tokenizer Get(string? deploymentName)
    {
        return _byDeployment.GetOrAdd(deploymentName ?? string.Empty, Build);
    }

    #region Private methods

    private Tokenizer Build(string deploymentName)
    {
        if (!string.IsNullOrWhiteSpace(deploymentName))
        {
            try
            {
                var tokenizer = TiktokenTokenizer.CreateForModel(deploymentName);

                LogUsingLibraryTokenizer(_logger, deploymentName);

                return tokenizer;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                LogTokenizerFallback(_logger, deploymentName, _options.FallbackEncoding);
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

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Estimating tokens for '{DeploymentName}' with the tokenizer the library reports for it")]
    private static partial void LogUsingLibraryTokenizer(ILogger logger, string deploymentName);

    [LoggerMessage(Level = LogLevel.Information, Message = "'{DeploymentName}' is not a recognized tokenizer model name (it is likely a deployment alias); falling back to the {Encoding} encoding")]
    private static partial void LogTokenizerFallback(ILogger logger, string deploymentName, string encoding);

    #endregion
}
