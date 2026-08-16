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
        public List<ConversationMessageDto> Messages { get; set; } = [];

        /// <summary>
        /// How many messages the whole transcript holds, including the system message that is never
        /// returned in <see cref="Messages"/>.
        /// </summary>
        /// <remarks>
        /// Read from the stored counter rather than counted, which is exact because the write that
        /// creates a turn's messages and the patch that moves this counter are one transaction.
        /// </remarks>
        public long TotalCount { get; set; }

        /// <summary>
        /// Whether older messages remain beyond the ones returned.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Always <see langword="false"/> for an unpaged request, which returns the whole
        /// transcript.
        /// </para>
        /// <para>
        /// This, and not the size of <see cref="Messages"/>, is what a paged walk terminates on. A
        /// page can legitimately hold fewer messages than were asked for — system messages are
        /// filtered out after the page is taken, so the oldest page of a transcript comes back one
        /// short, and a page holding only the system message comes back empty.
        /// </para>
        /// </remarks>
        public bool HasMore { get; set; }
    }
}
