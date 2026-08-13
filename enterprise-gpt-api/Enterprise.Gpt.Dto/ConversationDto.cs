namespace Enterprise.Gpt.Dto
{
    public class ConversationDto
    {
        public Guid Id { get; set; }

        /// <summary>
        /// The project this conversation belongs to, or <see langword="null"/> when it is standalone.
        /// </summary>
        public Guid? ProjectId { get; set; }

        /// <summary>
        /// The model that served the conversation's most recent turn, or <see langword="null"/>
        /// before its first turn.
        /// </summary>
        public Guid? ModelId { get; set; }

        public string Name { get; set; } = null!;

        public bool IsFavorite { get; set; }

        public DateTimeOffset DateCreated { get; set; }

        public DateTimeOffset DateModified { get; set; }
    }

    /// <summary>
    /// A single conversation read on its own, carrying the selections needed to resume it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ConversationDto"/> because <see cref="McpServerIds"/> costs a
    /// subquery per conversation; advertising it on the listing shape would either pay that cost
    /// for every page or return an empty list clients could not distinguish from "none selected".
    /// </remarks>
    public class ConversationDetailDto : ConversationDto
    {
        /// <summary>
        /// The MCP servers attached to the conversation's most recent turn. Empty when no turn has
        /// run yet or none were selected.
        /// </summary>
        public List<Guid> McpServerIds { get; set; } = [];
    }

    public class ChatConversationDto : ConversationDto
    {
        /// <summary>
        /// Gets or sets one page of the conversation's messages, oldest first.
        /// </summary>
        public List<ConversationMessageDto> Messages { get; set; } = [];

        /// <summary>
        /// Gets or sets how many message positions the conversation has allocated in total.
        /// </summary>
        /// <remarks>
        /// Counts every message ever written, including the system message this response never
        /// returns, so it is a progress indicator rather than the length of a filtered list.
        /// </remarks>
        public long TotalMessageCount { get; set; }

        /// <summary>
        /// Gets or sets whether older messages remain beyond this page.
        /// </summary>
        public bool HasMore { get; set; }
    }
}
