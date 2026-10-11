using Enterprise.Gpt.Service.Documents.Extraction.Interfaces;
using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

/// <remarks>
/// Accepted extensions are deliberately not configurable: they derive from the registered
/// <see cref="IDocumentTextExtractor"/> implementations, so configuration cannot advertise an unreadable
/// format.
/// </remarks>
public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    /// <summary>Document Intelligence caps inputs at 500 MB on S0 and 4 MB on F0.</summary>
    public long MaxFileSizeBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// Memory budget for queued uploads, each held whole until a worker takes it. Queue capacity derives from
    /// it, so this bounds burst exposure; in-flight jobs are bounded by <c>BackgroundJobs:MaxConcurrent</c>.
    /// </summary>
    public long MaxQueuedBytes { get; set; } = 512L * 1024 * 1024;

    public int MaxQueueLength => (int)Math.Max(1, Math.Min(int.MaxValue, MaxQueuedBytes / Math.Max(1, MaxFileSizeBytes)));

    public int EmbeddingBatchSize { get; set; } = 64;

    /// <summary>
    /// Minutes, not hours, because the link is a bearer credential. Signing already back-dates the start for
    /// clock skew, so the whole window is usable.
    /// </summary>
    public int DownloadUrlLifetimeMinutes { get; set; } = 5;

    public ChunkingOptions Chunking { get; set; } = new();
}

public sealed class DocumentOptionsValidator : AbstractValidator<DocumentOptions>
{
    public DocumentOptionsValidator()
    {
        RuleFor(options => options.MaxFileSizeBytes).InclusiveBetween(1L, 500L * 1024 * 1024)
            .WithMessage("Documents:MaxFileSizeBytes must be between 1 and 524288000.");
        RuleFor(options => options.MaxQueuedBytes).InclusiveBetween(1024L * 1024, 8L * 1024 * 1024 * 1024)
            .WithMessage("Documents:MaxQueuedBytes must be between 1048576 and 8589934592.");
        RuleFor(options => options.EmbeddingBatchSize).InclusiveBetween(1, 512)
            .WithMessage("Documents:EmbeddingBatchSize must be between 1 and 512.");
        RuleFor(options => options.DownloadUrlLifetimeMinutes).InclusiveBetween(1, 60)
            .WithMessage("Documents:DownloadUrlLifetimeMinutes must be between 1 and 60.");
        RuleFor(options => options.Chunking).SetValidator(new ChunkingOptionsValidator());
    }
}
