using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts.Models;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// A conversation as the export renderers see it.
/// </summary>
/// <remarks>
/// Only what the transcript persists: no activity cards, reasoning or partial answers. <c>Stored</c> travels
/// too because <c>format=json</c> serializes the storage shape; other renderers read <see cref="Messages"/>.
/// </remarks>
public sealed record ConversationExportDocument(
    TranscriptHeaderDocument Header,
    IReadOnlyList<TranscriptMessageDocument> Stored,
    DateTimeOffset ExportedAt)
{
    public string Name => Header.Name;

    /// <summary>Prompts and completed answers, oldest first.</summary>
    public IReadOnlyList<ConversationExportMessage> Messages { get; } =
    [
        .. Stored.Select(message => new ConversationExportMessage(
            message.Role,
            message.Role is ChatRoles.User ? "You" : "Assistant",
            message.Content))
    ];
}
