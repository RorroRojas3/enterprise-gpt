using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

/// <remarks>
/// Defaults assume 512-token chunks with 128 overlap; changing <c>Documents:Chunking</c> quietly changes what
/// <see cref="NeighborWindow"/> and <see cref="MaxResultTokens"/> ground an answer in. Retrieval is exact kNN
/// because candidates are first narrowed to one conversation and at most one project.
/// </remarks>
public sealed class DocumentRetrievalOptions
{
    public const string SectionName = "Documents:Retrieval";

    /// <summary>Vector-pass candidates before fusion.</summary>
    public int CandidateCount { get; set; } = 40;

    /// <summary>Keyword-pass candidates before fusion.</summary>
    public int LexicalCandidateCount { get; set; } = 40;

    public int MaxResults { get; set; } = 8;

    /// <summary>
    /// Chunks pulled either side of a match to restore an explanation a boundary cut; the overlap is removed,
    /// not duplicated, on merge. Zero disables expansion.
    /// </summary>
    public int NeighborWindow { get; set; } = 1;

    /// <summary>
    /// Largest cosine distance (in [0, 2]; 0.62 is ~0.38 similarity) for a vector-only match. kNN always
    /// returns something, so without this gate unanswerable questions get least-bad chunks as evidence.
    /// </summary>
    public double MaxDistance { get; set; } = 0.62;

    public int MaxPassagesPerDocument { get; set; } = 3;

    /// <summary>Passage-text budget per search, counted with the ingestion tokenizer.</summary>
    public int MaxResultTokens { get; set; } = 3000;

    public int MaxQueryTerms { get; set; } = 8;

    /// <summary>Short because retrieval runs inside a live chat turn and must fail fast.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Runs the keyword pass alongside the vector pass, since embeddings are weak at exact tokens such as
    /// part numbers, error codes and surnames.
    /// </summary>
    public bool EnableLexicalSearch { get; set; } = true;
}

public sealed class DocumentRetrievalOptionsValidator : AbstractValidator<DocumentRetrievalOptions>
{
    public DocumentRetrievalOptionsValidator()
    {
        RuleFor(options => options.CandidateCount).InclusiveBetween(1, 500)
            .WithMessage("Documents:Retrieval:CandidateCount must be between 1 and 500.");
        RuleFor(options => options.LexicalCandidateCount).InclusiveBetween(1, 500)
            .WithMessage("Documents:Retrieval:LexicalCandidateCount must be between 1 and 500.");
        RuleFor(options => options.MaxResults).InclusiveBetween(1, 50)
            .WithMessage("Documents:Retrieval:MaxResults must be between 1 and 50.");
        RuleFor(options => options.NeighborWindow).InclusiveBetween(0, 5)
            .WithMessage("Documents:Retrieval:NeighborWindow must be between 0 and 5.");
        RuleFor(options => options.MaxDistance).InclusiveBetween(0d, 2d)
            .WithMessage("Documents:Retrieval:MaxDistance must be between 0 and 2.");
        RuleFor(options => options.MaxPassagesPerDocument).InclusiveBetween(1, 50)
            .WithMessage("Documents:Retrieval:MaxPassagesPerDocument must be between 1 and 50.");
        RuleFor(options => options.MaxResultTokens).InclusiveBetween(256, 32000)
            .WithMessage("Documents:Retrieval:MaxResultTokens must be between 256 and 32000.");
        RuleFor(options => options.MaxQueryTerms).InclusiveBetween(1, 16)
            .WithMessage("Documents:Retrieval:MaxQueryTerms must be between 1 and 16.");
        RuleFor(options => options.CommandTimeoutSeconds).InclusiveBetween(1, 120)
            .WithMessage("Documents:Retrieval:CommandTimeoutSeconds must be between 1 and 120.");
        // Over-fetching is what gives rank fusion and the per-document cap something to choose between.
        RuleFor(options => options.CandidateCount).GreaterThanOrEqualTo(options => options.MaxResults)
            .WithMessage("Documents:Retrieval:CandidateCount must be at least Documents:Retrieval:MaxResults.");
        RuleFor(options => options.LexicalCandidateCount).GreaterThanOrEqualTo(options => options.MaxResults)
            .WithMessage("Documents:Retrieval:LexicalCandidateCount must be at least Documents:Retrieval:MaxResults.");
    }
}
