using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// A data row of a sheet belonging to a conversation document.
/// </summary>
[Table(nameof(ConversationDocumentSheetRow), Schema = "Core")]
public class ConversationDocumentSheetRow : BaseDocumentSheetRow
{
    /// <summary>The sheet this row belongs to.</summary>
    public Guid ConversationDocumentSheetId { get; set; }

    public ConversationDocumentSheet ConversationDocumentSheet { get; set; } = null!;
}
