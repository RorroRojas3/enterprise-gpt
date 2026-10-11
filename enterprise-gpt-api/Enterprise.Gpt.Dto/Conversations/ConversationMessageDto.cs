using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto.Conversations;

public class ConversationMessageDto
{
    public Guid Id { get; set; }

    /// <summary>
    /// When the message was produced: a user message when its prompt arrived, an assistant
    /// message when its answer completed.
    /// </summary>
    /// <remarks>Also the <c>?before=</c> cursor for backward paging.</remarks>
    public DateTimeOffset DateCreated { get; set; }

    public string Text { get; set; } = null!;

    public ChatRoles Role { get; set; }

    /// <summary>
    /// The message rendered to HTML when it was stored, or <see langword="null"/> when it was
    /// stored before server-side rendering existed or rendering failed.
    /// </summary>
    /// <remarks>
    /// When null the client renders <see cref="Text"/> itself. An export-fidelity artefact, not a mirror of
    /// the client's rendering: whitespace and highlighting markup differ.
    /// </remarks>
    public string? HtmlContent { get; set; }

    /// <summary>
    /// What the message contributes to the prompt on every future turn. Zero for a message
    /// stored before per-message token accounting existed.
    /// </summary>
    /// <remarks>
    /// Context cost, not billing: tool-call arguments are billed but never transcribed, so this will not
    /// equal the turn's output tokens.
    /// </remarks>
    public long Tokens { get; set; }

    /// <summary>
    /// How <see cref="Tokens"/> was arrived at.
    /// </summary>
    /// <remarks>
    /// A property-level string converter: a global one would turn <see cref="Role"/> into a string and
    /// break every client reading it.
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<TokenAccuracies>))]
    public TokenAccuracies TokenAccuracy { get; set; }

    /// <summary>
    /// What the turn that produced this message was billed, or <see langword="null"/> when the
    /// message is not an assistant message or was stored before per-message usage existed.
    /// </summary>
    /// <remarks>
    /// Deliberately disagrees with <see cref="Tokens"/>: this is the whole turn's charge, every tool, MCP
    /// call and agent underneath included.
    /// </remarks>
    public ConversationMessageUsageDto? Usage { get; set; }

    /// <summary>
    /// The reader's verdict on this message, or <see langword="null"/> when it has never been
    /// rated, the rating was withdrawn, or the message is not an assistant message.
    /// </summary>
    public ConversationMessageFeedbackDto? Feedback { get; set; }

    /// <summary>
    /// The files this message introduced, empty when it introduced none.
    /// </summary>
    /// <remarks>Identities only: links are minted on click, so nothing here expires or is secret.</remarks>
    public IReadOnlyList<MessageAttachmentDto> Attachments { get; set; } = [];
}
