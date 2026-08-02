using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Settings
{
    /// <summary>
    /// Tuning knobs for the document ingestion pipeline, bound from the <c>Documents</c> configuration
    /// section and validated at startup.
    /// </summary>
    /// <remarks>
    /// The set of accepted file extensions is deliberately <em>not</em> configurable: it is derived from
    /// the registered <see cref="Extraction.IDocumentTextExtractor"/> implementations, so configuration can
    /// never advertise a format nothing knows how to read.
    /// </remarks>
    public sealed class DocumentOptions
    {
        /// <summary>
        /// Configuration section this type binds from.
        /// </summary>
        public const string SectionName = "Documents";

        /// <summary>
        /// Largest upload accepted, in bytes. Default: 50 MB. Azure Document Intelligence caps paid-tier
        /// (S0) inputs at 500 MB and free-tier (F0) inputs at 4 MB.
        /// </summary>
        [Range(1, 500L * 1024 * 1024)]
        public long MaxFileSizeBytes { get; set; } = 50L * 1024 * 1024;

        /// <summary>
        /// Memory budget for uploads waiting to be processed, in bytes. Default: 512 MB.
        /// </summary>
        /// <remarks>
        /// Every queued item holds its whole file in memory until a worker picks it up, so this — not a
        /// count of items — is what actually bounds the host's exposure to a burst of uploads. The queue's
        /// capacity is derived as <see cref="MaxQueuedBytes"/> ÷ <see cref="MaxFileSizeBytes"/>, so the
        /// worst case really is this many bytes; writers wait once it is full, pushing backpressure out to
        /// the caller's request. In-flight jobs are additional and bounded separately by
        /// <c>BackgroundJobs:MaxConcurrent</c>.
        /// </remarks>
        [Range(1024L * 1024, 8L * 1024 * 1024 * 1024)]
        public long MaxQueuedBytes { get; set; } = 512L * 1024 * 1024;

        /// <summary>
        /// Number of uploads that can wait in the queue, derived from the byte budget so the memory
        /// ceiling is a property of the configuration rather than an assumption. Always at least one.
        /// </summary>
        public int MaxQueueLength => (int)Math.Max(1, Math.Min(int.MaxValue, MaxQueuedBytes / Math.Max(1, MaxFileSizeBytes)));

        /// <summary>
        /// Number of chunks sent to the embedding model per request. Default: 64.
        /// </summary>
        [Range(1, 512)]
        public int EmbeddingBatchSize { get; set; } = 64;

        /// <summary>
        /// Chunking parameters.
        /// </summary>
        public ChunkingOptions Chunking { get; set; } = new();
    }

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
}
