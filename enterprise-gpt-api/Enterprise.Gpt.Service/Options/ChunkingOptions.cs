using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Options;

/// <summary>
/// Token-based chunking parameters, bound from the <c>Documents:Chunking</c> configuration section.
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>
    /// Maximum number of tokens in a chunk. Default: 512.
    /// </summary>
    [Range(1, 8192)]
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// Number of tokens carried from the end of one chunk into the start of the next, so a fact
    /// straddling a boundary still appears whole in at least one chunk. Must be smaller than
    /// <see cref="MaxTokens"/>. Default: 128.
    /// </summary>
    [Range(0, 8191)]
    public int OverlapTokens { get; set; } = 128;
}
