using System.ComponentModel;

namespace Enterprise.Gpt.Dto.Enums
{
    /// <summary>
    /// Stages of the document ingestion pipeline, reported to clients polling for upload progress.
    /// </summary>
    /// <remarks>
    /// Values 1–6 are frozen: clients already deployed compare against them. New stages are appended, never
    /// renumbered, and are projected onto the coarse job state so an older client still sees a sensible
    /// Enqueued/Processing/Succeeded/Failed progression — <see cref="Cancelled"/> projects onto Failed
    /// rather than widening that vocabulary.
    /// </remarks>
    public enum JobStatus
    {
        [Description(nameof(Queued))]
        Queued = 1,

        [Description(nameof(Uploading))]
        Uploading = 2,

        [Description(nameof(Extracting))]
        Extracting = 3,

        [Description(nameof(Embedding))]
        Embedding = 4,

        [Description(nameof(Processed))]
        Processed = 5,

        [Description(nameof(Failed))]
        Failed = 6,

        [Description(nameof(Chunking))]
        Chunking = 7,

        [Description(nameof(Persisting))]
        Persisting = 8,

        /// <summary>The caller called the upload off. Terminal, and reported to clients as Failed.</summary>
        [Description(nameof(Cancelled))]
        Cancelled = 9,
    }
}
