using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// Base type for the one canonical summary of an ingested document: the text a summarization run
/// produced, together with what produced it and what it cost.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="BaseDocumentChunk"/>'s split — an unmapped base plus one mapped table per
/// document family — because the two document families are structurally identical and differ only
/// in which document they hang off.
/// </para>
/// <para>
/// Exactly one live row per document. A regeneration rewrites the row in place rather than adding a
/// second, so <see cref="BaseModifiedEntity.DateModified"/> is "when this summary was last
/// generated" and there is no version history to reconcile.
/// </para>
/// </remarks>
public class BaseDocumentSummary : BaseModifiedEntity
{
    /// <summary>
    /// The summary text, exactly as the final reduce (or the single-pass call) produced it.
    /// </summary>
    public string Text { get; set; } = null!;

    /// <summary>
    /// The catalog model that produced this summary, captured at generation time.
    /// </summary>
    /// <remarks>
    /// Snapshotted for the reason <see cref="ConversationUsage.DeploymentName"/> is: repointing the
    /// configured summarizer later must not rewrite what an already-generated summary says produced
    /// it.
    /// </remarks>
    [ForeignKey(nameof(Model))]
    public Guid ModelId { get; set; }

    /// <summary>
    /// The deployment that actually served the run, on the same snapshot terms as
    /// <see cref="ModelId"/>.
    /// </summary>
    [StringLength(512)]
    public string DeploymentName { get; set; } = null!;

    /// <summary>
    /// The prompt-template version this summary was generated under.
    /// </summary>
    /// <remarks>
    /// This is the whole cache-invalidation mechanism. A document's text never changes after
    /// ingestion, so the only thing that can stale a summary is a change to the prompts that
    /// produced it; a row whose version no longer matches the running one is treated as a miss and
    /// regenerated. Without it the cache would need an explicit regenerate flag, and a flag the
    /// model could set is a flag the model can spend money with.
    /// </remarks>
    [StringLength(16)]
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
    /// How many window-sized map units the document was split into, or <c>0</c> on the single-pass
    /// path.
    /// </summary>
    public int MapUnitCount { get; set; }

    /// <summary>
    /// How many collapse passes ran before the partial summaries fitted the reduce budget. Usually
    /// <c>0</c> — the loop is conditional, not a mandatory extra call.
    /// </summary>
    public int CollapsePasses { get; set; }

    public Model Model { get; set; } = null!;
}
