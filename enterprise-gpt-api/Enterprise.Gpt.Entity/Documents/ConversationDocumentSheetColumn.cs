using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// A column of a sheet belonging to a conversation document.
/// </summary>
public class ConversationDocumentSheetColumn : BaseDocumentSheetColumn<Guid>
{
    /// <summary>The sheet this column belongs to.</summary>
    public Guid ConversationDocumentSheetId { get; set; }

    public ConversationDocumentSheet ConversationDocumentSheet { get; set; } = null!;
}
