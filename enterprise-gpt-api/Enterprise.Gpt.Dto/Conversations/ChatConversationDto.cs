namespace Enterprise.Gpt.Dto.Conversations;

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
