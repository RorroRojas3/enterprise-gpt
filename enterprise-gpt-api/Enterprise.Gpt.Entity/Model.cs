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

        public Provider Provider { get; set; } = null!;
    }
}
