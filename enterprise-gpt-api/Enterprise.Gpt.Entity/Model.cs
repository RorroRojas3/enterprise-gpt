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

        /// <summary>
        /// Gets or sets the price in USD charged for one million input tokens, or
        /// <see langword="null"/> when the model has no price on file.
        /// </summary>
        /// <remarks>
        /// Quoted per 1,000,000 tokens because provider price sheets are. Nullable so "unpriced"
        /// stays distinguishable from "free": a null suppresses a cost figure entirely, where a
        /// zero asserts the call cost nothing. Six decimal places because per-million prices run
        /// to fractions of a cent.
        /// </remarks>
        [Column(TypeName = "decimal(18, 6)")]
        public decimal? InputPricePerMillionTokens { get; set; }

        /// <summary>
        /// Gets or sets the price in USD charged for one million output tokens, on the same terms
        /// as <see cref="InputPricePerMillionTokens"/>.
        /// </summary>
        [Column(TypeName = "decimal(18, 6)")]
        public decimal? OutputPricePerMillionTokens { get; set; }

        public bool IsToolEnabled { get; set; } = true;

        public bool IsDefault { get; set; } = false;

        public Provider Provider { get; set; } = null!;
    }
}
