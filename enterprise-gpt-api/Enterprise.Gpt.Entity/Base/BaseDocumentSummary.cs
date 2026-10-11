using Enterprise.Gpt.Entity.ChatModels;

namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// Base type for the one canonical summary of an ingested document: the text a summarization run
/// produced, together with what produced it and what it cost.
/// </summary>
/// <remarks>
/// Exactly one live row per document. A regeneration rewrites the row in place, so
/// <see cref="BaseModifiedEntity{TKey}.DateModified"/> is when this summary was last generated.
/// </remarks>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseDocumentSummary<TKey> : BaseModifiedEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <summary>
    /// The summary text, exactly as the final reduce (or the single-pass call) produced it.
    /// </summary>
    public string Text { get; set; } = null!;

    /// <summary>
    /// The catalog model that produced this summary, captured at generation time.
    /// </summary>
    /// <remarks>
    /// Snapshotted so that repointing the configured summarizer later does not rewrite what an
    /// already-generated summary says produced it.
    /// </remarks>
    public Guid ModelId { get; set; }

    /// <summary>
    /// The deployment that actually served the run, on the same snapshot terms as <see cref="ModelId"/>.
    /// </summary>
    public string DeploymentName { get; set; } = null!;

    /// <summary>
    /// The prompt-template version this summary was generated under.
    /// </summary>
    /// <remarks>
    /// The whole cache-invalidation mechanism: a document's text never changes after ingestion, so a row
    /// whose version no longer matches the running one is treated as a miss and regenerated.
    /// </remarks>
    public string PromptVersion { get; set; } = null!;

    /// <summary>
    /// Input tokens the whole run consumed, summed across every map, collapse and reduce call.
    /// </summary>
    public long InputTokens { get; set; }

    /// <summary>
    /// Output tokens the whole run produced, on the same terms as <see cref="InputTokens"/>.
    /// </summary>
    public long OutputTokens { get; set; }

    /// <summary>
    /// How many model calls the run made: every map call, every collapse-pass call, and the final
    /// reduce. Exactly <c>1</c> when the document fitted a single pass.
    /// </summary>
    public int ModelCallCount { get; set; }

    /// <summary>
    /// How many window-sized map units the document was split into, or <c>0</c> on the single-pass path.
    /// </summary>
    public int MapUnitCount { get; set; }

    /// <summary>
    /// How many collapse passes ran before the partial summaries fitted the reduce budget.
    /// </summary>
    public int CollapsePasses { get; set; }

    public Model Model { get; set; } = null!;
}
