using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Supplies the real chunker to tests that need true token counts — row-window sizing is measured
/// against the same budget the application chunks with, so a stub would prove nothing.
/// </summary>
public static class TestChunker
{
    // Building a tokenizer indexes a large vocabulary; shared here for the reason the application
    // registers the chunker as a singleton.
    private static readonly Lazy<TokenTextChunker> _default = new(() => Create());

    /// <summary>
    /// A chunker configured the way <c>appsettings.json</c> configures the application's.
    /// </summary>
    public static TokenTextChunker Default => _default.Value;

    public static TokenTextChunker Create(int maxTokens = 512, int overlapTokens = 128)
    {
        var options = Options.Create(new DocumentOptions
        {
            Chunking = new ChunkingOptions { MaxTokens = maxTokens, OverlapTokens = overlapTokens }
        });

        var azureOpenAIOptions = Options.Create(new AzureOpenAIOptions { EmbeddingModel = "text-embedding-3-small" });

        return new TokenTextChunker(options, azureOpenAIOptions, NullLogger<TokenTextChunker>.Instance);
    }
}
