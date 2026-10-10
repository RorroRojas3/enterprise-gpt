namespace Enterprise.Gpt.Dto.Conversations;

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
