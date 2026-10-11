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
    /// Off by default: a non-reasoning deployment rejects the whole request, and reasoning tokens bill
    /// as output. Read only by the Azure OpenAI Responses client; Azure AI Foundry discards it.
    /// </remarks>
    public bool IsReasoningEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this deployment is offered in the chat model
    /// picker.
    /// </summary>
    /// <remarks>
    /// Visibility, not authorization: only <c>GET api/models</c> filters on it, and a turn naming a hidden
    /// model (e.g. the pinned summarizer) is still served; retiring a model is <c>DateDeactivated</c>'s job.
    /// Defaulted to <see langword="true"/> in the store too, so rows written without the column stay visible.
    /// </remarks>
    public bool IsUserSelectable { get; set; } = true;

    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets what this deployment charges per 1,000,000 input tokens, or
    /// <see langword="null"/> when no price has been recorded.
    /// </summary>
    /// <remarks>
    /// Null rather than zero so an unpriced model is distinguishable from a free one. USD, at six decimals
    /// as price sheets quote it. The <em>current</em> price only; usage rows snapshot it per call.
    /// </remarks>
    public decimal? InputPricePerMillionTokens { get; set; }

    /// <summary>
    /// Gets or sets what this deployment charges per 1,000,000 output tokens, on the same terms
    /// as <see cref="InputPricePerMillionTokens"/>.
    /// </summary>
    public decimal? OutputPricePerMillionTokens { get; set; }

    public Provider Provider { get; set; } = null!;
}
