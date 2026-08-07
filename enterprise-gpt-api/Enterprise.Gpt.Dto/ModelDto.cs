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

        public bool IsDefault { get; init; }

        public DateTimeOffset? DateDeactivated { get; init; }
    }
}
