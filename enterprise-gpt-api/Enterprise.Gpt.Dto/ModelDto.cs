namespace Enterprise.Gpt.Dto
{
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

        public bool IsDefault { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 input tokens in USD, or <see langword="null"/>
        /// when no price has been recorded. Null means unpriced, not free.
        /// </summary>
        public decimal? InputPricePerMillionTokens { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 output tokens in USD, on the same terms as
        /// <see cref="InputPricePerMillionTokens"/>.
        /// </summary>
        public decimal? OutputPricePerMillionTokens { get; init; }

        public DateTimeOffset? DateDeactivated { get; init; }
    }
}
