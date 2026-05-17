namespace Enterprise.Gpt.Dto
{
    public record ModelDto
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = null!;

        public string Description { get; init; } = null!;

        public bool IsToolEnabled { get; init; }

        public bool IsFavorite { get; init; }
    }
}
