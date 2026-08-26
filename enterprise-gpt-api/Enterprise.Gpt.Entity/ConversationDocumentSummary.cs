using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// The canonical summary of a document uploaded into a conversation.
/// </summary>
[Table(nameof(ConversationDocumentSummary), Schema = "Core")]
public class ConversationDocumentSummary : BaseDocumentSummary
{
    /// <summary>The document this summarizes.</summary>
    public Guid ConversationDocumentId { get; set; }

    public ConversationDocument ConversationDocument { get; set; } = null!;
}
