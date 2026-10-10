using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

/// <summary>
/// Token-based chunking parameters, bound from the <c>Documents:Chunking</c> configuration section.
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>
    /// Maximum number of tokens in a chunk. Default: 512.
    /// </summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// Number of tokens carried from the end of one chunk into the start of the next, so a fact
    /// straddling a boundary still appears whole in at least one chunk. Must be smaller than
    /// <see cref="MaxTokens"/>. Default: 128.
    /// </summary>
    public int OverlapTokens { get; set; } = 128;
}

/// <summary>Validates <see cref="ChunkingOptions"/>.</summary>
public sealed class ChunkingOptionsValidator : AbstractValidator<ChunkingOptions>
{
    public ChunkingOptionsValidator()
    {
        RuleFor(options => options.MaxTokens).InclusiveBetween(1, 8192)
            .WithMessage("Documents:Chunking:MaxTokens must be between 1 and 8192.");
        RuleFor(options => options.OverlapTokens).InclusiveBetween(0, 8191)
            .WithMessage("Documents:Chunking:OverlapTokens must be between 0 and 8191.");
        RuleFor(options => options.OverlapTokens).LessThan(options => options.MaxTokens)
            .WithMessage("Documents:Chunking:OverlapTokens must be smaller than Documents:Chunking:MaxTokens.");
    }
}
