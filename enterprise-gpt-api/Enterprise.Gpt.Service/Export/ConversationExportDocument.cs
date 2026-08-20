using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts;

namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// One message of a conversation, reduced to what a rendered document needs.
/// </summary>
/// <param name="Role">Who produced the message.</param>
/// <param name="Label">The heading an export writes above it — <c>You</c> or <c>Assistant</c>.</param>
/// <param name="Markdown">The message text as authored, which a markdown export emits verbatim.</param>
/// <param name="Html">
/// The HTML stored beside the message at persist time, or <see langword="null"/> for a message written
/// before server-side rendering existed.
/// </param>
public sealed record ConversationExportMessage(ChatRoles Role, string Label, string Markdown, string? Html);

/// <summary>
/// A conversation as the export renderers see it.
/// </summary>
/// <param name="Header">The conversation's transcript header.</param>
/// <param name="Stored">The transcript's prompts and completed answers, oldest first.</param>
/// <param name="ExportedAt">When the export was produced.</param>
/// <remarks>
/// <para>
/// Carries no activity cards, no reasoning text and no unsaved partial answer — that is the whole of
/// what the transcript persists, so it is the whole of what an export can honestly contain.
/// </para>
/// <para>
/// The stored documents travel alongside the reduced view because one renderer is specifically
/// <em>about</em> them: <c>format=json</c> is documented as "the storage shape rather than a third
/// representation of it", so it serializes these, while every other renderer reads
/// <see cref="Messages"/> and never learns that Cosmos exists.
/// </para>
/// </remarks>
public sealed record ConversationExportDocument(
    TranscriptHeaderDocument Header,
    IReadOnlyList<TranscriptMessageDocument> Stored,
    DateTimeOffset ExportedAt)
{
    /// <summary>Gets the conversation's display name.</summary>
    public string Name => Header.Name;

    /// <summary>Gets the prompts and completed answers, oldest first.</summary>
    public IReadOnlyList<ConversationExportMessage> Messages { get; } =
    [
        .. Stored.Select(message => new ConversationExportMessage(
            message.Role,
            message.Role is ChatRoles.User ? "You" : "Assistant",
            message.Content,
            message.HtmlContent))
    ];
}
