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
    }
}
