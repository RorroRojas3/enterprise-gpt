using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

/// <summary>Token-based chunking, bound from <c>Documents:Chunking</c>.</summary>
public sealed class ChunkingOptions
{
    public int MaxTokens { get; set; } = 512;

    /// <summary>Carried into the next chunk so a fact straddling a boundary appears whole in one.</summary>
    public int OverlapTokens { get; set; } = 128;
}

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
