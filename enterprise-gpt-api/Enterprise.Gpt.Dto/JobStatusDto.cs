namespace RR.AI_Chat.Dto
{
    public class JobStatusDto
    {
        public string Id { get; set; } = null!;

        public string State { get; set; } = null!;

        public string Status { get; set; } = null!;

        public int Progress { get; set; } = 0;
    }
}
