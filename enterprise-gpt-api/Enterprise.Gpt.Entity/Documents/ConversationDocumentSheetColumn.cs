using System.ComponentModel.DataAnnotations.Schema;
using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// A column of a sheet belonging to a conversation document.
/// </summary>
[Table(nameof(ConversationDocumentSheetColumn), Schema = "Core")]
public class ConversationDocumentSheetColumn : BaseDocumentSheetColumn
{
    /// <summary>The sheet this column belongs to.</summary>
    public Guid ConversationDocumentSheetId { get; set; }

    public ConversationDocumentSheet ConversationDocumentSheet { get; set; } = null!;
}
