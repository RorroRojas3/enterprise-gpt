using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto
{
    public class ConversationMessageDto
    {
        /// <summary>
        /// The message's identifier.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// When the message was produced: a user message when its prompt arrived, an assistant
        /// message when its answer completed.
        /// </summary>
        /// <remarks>
        /// Also the cursor for backward paging. A client walking a long transcript passes the
        /// oldest value it holds as <c>?before=</c>, so this has to be on the wire for that
        /// parameter to be usable at all.
        /// </remarks>
        public DateTimeOffset DateCreated { get; set; }

        public string Text { get; set; } = null!;

        public ChatRoles Role { get; set; }

        /// <summary>
        /// The message rendered to HTML when it was stored, or <see langword="null"/> when it was
        /// stored before server-side rendering existed or rendering failed.
        /// </summary>
        /// <remarks>
        /// A null value means the client must render <see cref="Text"/> itself. This is an
        /// export-fidelity artefact rather than a mirror of what the client shows: the two render
        /// through different pipelines and differ in whitespace and syntax-highlighting markup.
        /// </remarks>
        public string? HtmlContent { get; set; }

        /// <summary>
        /// What the message contributes to the prompt on every future turn. Zero for a message
        /// stored before per-message token accounting existed.
        /// </summary>
        /// <remarks>
        /// The context cost, not what the provider billed. On an assistant message it counts only
        /// the transcribed answer, so it will not equal the turn's reported output tokens — tool
        /// call arguments are generated and billed but never transcribed.
        /// </remarks>
        public long Tokens { get; set; }

        /// <summary>
        /// How <see cref="Tokens"/> was arrived at.
        /// </summary>
        /// <remarks>
        /// Serialized as a string, unlike <see cref="Role"/>. The converter is applied to this
        /// property alone rather than to the API's serializer options, because a global string-enum
        /// converter would change <see cref="Role"/> from a number to a string and break every
        /// client reading it.
        /// </remarks>
        [JsonConverter(typeof(JsonStringEnumConverter<TokenAccuracies>))]
        public TokenAccuracies TokenAccuracy { get; set; }

        /// <summary>
        /// What the turn that produced this message was billed, or <see langword="null"/> when the
        /// message is not an assistant message or was stored before per-message usage existed.
        /// </summary>
        /// <remarks>
        /// The counterpart to <see cref="Tokens"/> rather than a restatement of it, and the two
        /// deliberately disagree: <see cref="Tokens"/> is what this message's own text costs the
        /// prompt on every future turn, while this is what the provider charged for the whole turn
        /// — every tool round trip, MCP call and agent underneath it included. A turn that ran tools
        /// reports far more here than the answer beside it weighs.
        /// </remarks>
        public ConversationMessageUsageDto? Usage { get; set; }

        /// <summary>
        /// The reader's verdict on this message, or <see langword="null"/> when it has never been
        /// rated, the rating was withdrawn, or the message is not an assistant message (US-1102).
        /// </summary>
        public ConversationMessageFeedbackDto? Feedback { get; set; }

        /// <summary>
        /// The files this message introduced, empty when it introduced none.
        /// </summary>
        /// <remarks>
        /// Identities only. A client asks the document download route for a link at the moment of the
        /// click, so nothing here expires and nothing here is a credential.
        /// </remarks>
        public IReadOnlyList<MessageAttachmentDto> Attachments { get; set; } = [];
    }

    /// <summary>
    /// What a turn consumed, as it was recorded beside the assistant message it produced.
    /// </summary>
    /// <remarks>
    /// Carries the turn's totals rather than the assistant's share alone. The split between the
    /// assistant's own model turns and the tools underneath them lives in the relational audit
    /// trail, which is where a report reads it from; a reader looking at one message wants the one
    /// number that message cost.
    /// </remarks>
    public class ConversationMessageUsageDto
    {
        /// <summary>
        /// The turn's total input tokens, tools included.
        /// </summary>
        public long InputTokens { get; set; }

        /// <summary>
        /// The turn's total output tokens, tools included.
        /// </summary>
        public long OutputTokens { get; set; }
    }

    /// <summary>
    /// A reader's rating of one assistant message (US-1102).
    /// </summary>
    /// <remarks>
    /// A nested block rather than a bare <c>rating</c> field, so a later story can add a comment or
    /// a category beside the verdict without a breaking change to the message shape.
    /// </remarks>
    public class ConversationMessageFeedbackDto
    {
        /// <summary>
        /// The verdict.
        /// </summary>
        /// <remarks>
        /// Serialized as a string for the same reason
        /// <see cref="ConversationMessageDto.TokenAccuracy"/> is, and by the same property-level
        /// converter: the API registers no global string-enum converter, because one would turn
        /// <see cref="ConversationMessageDto.Role"/> from a number into a string.
        /// </remarks>
        [JsonConverter(typeof(JsonStringEnumConverter<MessageFeedbackRatings>))]
        public MessageFeedbackRatings Rating { get; set; }

        /// <summary>
        /// When the rating was last set.
        /// </summary>
        public DateTimeOffset DateModified { get; set; }
    }
}
