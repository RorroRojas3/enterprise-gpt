using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    [Table(nameof(Model), Schema = "Core.Ref")]
    public class Model : BaseModifiedByEntity
    {
        [ForeignKey(nameof(Provider))]
        public Guid ProviderId { get; set; }

        [StringLength(256)]
        public string Name { get; set; } = null!;

        /// <summary>
        /// Gets or sets the provider-side identifier sent as <c>ChatOptions.ModelId</c>: an Azure
        /// OpenAI deployment name, or a Bedrock model id, inference profile id, or ARN.
        /// </summary>
        /// <remarks>
        /// Sized for the longest of those — a Bedrock inference-profile ARN overruns 256 characters.
        /// </remarks>
        [StringLength(512)]
        public string DeploymentName { get; set; } = null!;

        [StringLength(1024)]
        public string Description { get; set; } = null!;

        [Column(TypeName = "decimal(18, 2)")]
        public decimal ContextWindowSize { get; set; }

        [Column(TypeName = "decimal(18, 2)")]
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
        public bool IsReasoningEnabled { get; set; } = false;

        public bool IsDefault { get; set; } = false;

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
        [Column(TypeName = "decimal(18, 6)")]
        public decimal? InputPricePerMillionTokens { get; set; }

        /// <summary>
        /// Gets or sets what this deployment charges per 1,000,000 output tokens, on the same terms
        /// as <see cref="InputPricePerMillionTokens"/>.
        /// </summary>
        [Column(TypeName = "decimal(18, 6)")]
        public decimal? OutputPricePerMillionTokens { get; set; }

        public Provider Provider { get; set; } = null!;
    }
}
