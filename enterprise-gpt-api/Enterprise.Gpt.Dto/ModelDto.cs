namespace RR.AI_Chat.Dto
{
    public class ModelDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = null!;

        public string Description { get; set; } = null!;

        public bool IsToolEnabled { get; set; } = false;
    }
}
