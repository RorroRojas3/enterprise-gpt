namespace RR.AI_Chat.Dto
{
    public class PaginatedResponseDto<T>
    {
        public List<T> Items { get; set; } = [];
        public int TotalCount { get; set; }
        public int PageSize { get; set; }
        public int CurrentPage { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    }
}
