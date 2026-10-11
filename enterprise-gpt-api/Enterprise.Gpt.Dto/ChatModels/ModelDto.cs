namespace Enterprise.Gpt.Dto.ChatModels;

public record ModelDto
{
    public Guid Id { get; init; }

    public Guid ProviderId { get; init; }

    public string Name { get; init; } = null!;

    public string DeploymentName { get; init; } = null!;

    public string Description { get; init; } = null!;

    public decimal ContextWindowSize { get; init; }

    public decimal MaxOutputTokens { get; init; }

    public bool IsToolEnabled { get; init; }

    public bool IsReasoningEnabled { get; init; }

    /// <summary>
    /// Whether the deployment is offered in the chat model picker; always <see langword="true"/> on
    /// <c>GET api/models</c>, which filters on it.
    /// </summary>
    public bool IsUserSelectable { get; init; }

    public bool IsDefault { get; init; }

    /// <summary>USD per 1,000,000 input tokens; <see langword="null"/> means unpriced, not free.</summary>
    public decimal? InputPricePerMillionTokens { get; init; }

    /// <summary>
    /// USD per 1,000,000 output tokens, on the same terms as <see cref="InputPricePerMillionTokens"/>.
    /// </summary>
    public decimal? OutputPricePerMillionTokens { get; init; }

    public DateTimeOffset? DateDeactivated { get; init; }
}
