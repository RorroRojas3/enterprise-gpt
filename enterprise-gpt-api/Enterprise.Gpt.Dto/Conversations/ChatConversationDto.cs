namespace Enterprise.Gpt.Dto.Conversations;

public class ChatConversationDto : ConversationDto
{
    public List<ConversationMessageDto> Messages { get; set; } = [];

    /// <summary>
    /// How many messages the whole transcript holds, including the system message that is never
    /// returned in <see cref="Messages"/>.
    /// </summary>
    /// <remarks>
    /// Read from the stored counter, which is exact: the message write and the counter patch are one
    /// transaction.
    /// </remarks>
    public long TotalCount { get; set; }

    /// <summary>
    /// Whether older messages remain beyond the ones returned; always <see langword="false"/> when unpaged.
    /// </summary>
    /// <remarks>
    /// End a paged walk on this, not on the size of <see cref="Messages"/>: system messages are filtered
    /// after the page is taken, so a page can come back short or empty.
    /// </remarks>
    public bool HasMore { get; set; }
}
