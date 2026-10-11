using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.ChatModels;

public class Model : BaseModifiedByEntity<Guid>
{
    public Guid ProviderId { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the provider-side identifier sent as <c>ChatOptions.ModelId</c>: an Azure
    /// OpenAI deployment name, or a Bedrock model id, inference profile id, or ARN.
    /// </summary>
    /// <remarks>
    /// Sized for the longest of those — a Bedrock inference-profile ARN overruns 256 characters.
    /// </remarks>
    public string DeploymentName { get; set; } = null!;

    public string Description { get; set; } = null!;

    public decimal ContextWindowSize { get; set; }

    public decimal MaxOutputTokens { get; set; }

    public bool IsToolEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether this deployment is asked for a reasoning
    /// summary on every turn.
    /// </summary>
    /// <remarks>
    /// Off by default because it is not a free capability: only reasoning models accept the
    /// option, and a deployment that does not support it rejects the whole request rather
    /// than ignoring the flag. It also costs — reasoning tokens are billed as output tokens.
    /// Provider-specific in effect, since only the Azure AI Foundry client reads it today.
    /// </remarks>
    public bool IsReasoningEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this deployment is offered in the chat model
    /// picker.
    /// </summary>
    /// <remarks>
    /// Governs <c>GET api/models</c>, the picker's source, and nothing else: the administrative
    /// <c>GET api/models/all</c> ignores it, so a hidden row stays findable and editable — the
    /// same two-routes-two-meanings split <c>DateDeactivated</c> already has.
    /// <para>
    /// Visibility, not authorization. A turn that names a hidden model by id is served
    /// normally, because the model lookup a turn performs filters on <c>DateDeactivated</c>
    /// alone. Hiding a purpose-built deployment — the pinned document summarizer — from the
    /// picker is what this exists for; retiring one is <c>DateDeactivated</c>'s job.
    /// </para>
    /// <para>
    /// Defaulted to <see langword="true"/> in the store as well as here, so a row written by
    /// any path that does not mention the column is visible rather than silently hidden.
    /// </para>
    /// </remarks>
    public bool IsUserSelectable { get; set; } = true;

    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets what this deployment charges per 1,000,000 input tokens, or
    /// <see langword="null"/> when no price has been recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nullable rather than defaulted to zero so an unpriced model stays distinguishable from a
    /// free one: a report that sums zeros silently understates spend, while one that skips
    /// nulls can say how much of its input it could not price.
    /// </para>
    /// <para>
    /// Per million tokens, and at six decimal places rather than the two the sibling columns
    /// use, because provider price sheets are quoted that way and a per-token rate rounded to
    /// cents is entirely rounding error. A single currency is assumed, and it is USD; a
    /// multi-currency deployment would need a currency column beside these.
    /// </para>
    /// <para>
    /// The catalog is editable, so this is the <em>current</em> price and not what any past
    /// call was charged. Every usage row snapshots the pair at write time for that reason.
    /// </para>
    /// </remarks>
    public decimal? InputPricePerMillionTokens { get; set; }

    /// <summary>
    /// Gets or sets what this deployment charges per 1,000,000 output tokens, on the same terms
    /// as <see cref="InputPricePerMillionTokens"/>.
    /// </summary>
    public decimal? OutputPricePerMillionTokens { get; set; }

    public Provider Provider { get; set; } = null!;
}
