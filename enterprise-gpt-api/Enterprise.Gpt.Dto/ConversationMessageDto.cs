using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto
{
    public class ConversationMessageDto
    {
        /// <summary>
        /// Gets or sets this message's position in the conversation.
        /// </summary>
        /// <remarks>
        /// The cursor for the previous page: pass the lowest sequence in a page back as
        /// <c>before</c> to fetch the messages that precede it. Positions can have gaps, so this is
        /// an ordering key rather than an index.
        /// </remarks>
        public long Sequence { get; set; }

        public string Text { get; set; } = null!;

        public ChatRoles Role { get; set; }

        /// <summary>
        /// Gets or sets the server-rendered HTML of <see cref="Text"/>, or <see langword="null"/>
        /// for a message stored before rendering existed.
        /// </summary>
        /// <remarks>
        /// A null means the client must render <see cref="Text"/> itself; it does not mean the
        /// message is empty.
        /// </remarks>
        public string? HtmlContent { get; set; }

        /// <summary>
        /// Gets or sets what this message contributes to the prompt on every future turn.
        /// </summary>
        /// <remarks>
        /// Zero for a message stored before estimation existed. On an assistant message this
        /// deliberately excludes tool spend, so it will not match what the turn was billed.
        /// </remarks>
        public long Tokens { get; set; }

        /// <summary>
        /// Gets or sets how <see cref="Tokens"/> was arrived at, or <see langword="null"/> for a
        /// message stored before estimation existed.
        /// </summary>
        public TokenAccuracies? TokenAccuracy { get; set; }
    }
}
