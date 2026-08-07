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

        public decimal ContextWindowSize { get; set; }

        public decimal MaxOutputTokens { get; set; }

        public bool IsToolEnabled { get; set; } = true;

        public bool IsDefault { get; set; } = false;

        public Provider Provider { get; set; } = null!;
    }
}
