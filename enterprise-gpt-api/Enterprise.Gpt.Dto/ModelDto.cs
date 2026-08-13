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

        /// <summary>
        /// Gets the price in USD per one million input tokens, or <see langword="null"/> when the
        /// model has no price on file. A null is not a zero: it means unpriced, not free.
        /// </summary>
        public decimal? InputPricePerMillionTokens { get; init; }

        /// <summary>
        /// Gets the price in USD per one million output tokens, on the same terms as
        /// <see cref="InputPricePerMillionTokens"/>.
        /// </summary>
        public decimal? OutputPricePerMillionTokens { get; init; }

        public bool IsToolEnabled { get; init; }

        public bool IsDefault { get; init; }

        public DateTimeOffset? DateDeactivated { get; init; }
    }
}
