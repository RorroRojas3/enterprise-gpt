using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// A worksheet of a spreadsheet uploaded into a conversation.
/// </summary>
[Table(nameof(ConversationDocumentSheet), Schema = "Core")]
public class ConversationDocumentSheet : BaseDocumentSheet
{
    /// <summary>The document this sheet belongs to.</summary>
    public Guid ConversationDocumentId { get; set; }

    public ConversationDocument ConversationDocument { get; set; } = null!;

    /// <summary>The sheet's columns, in left-to-right order.</summary>
    public List<ConversationDocumentSheetColumn> Columns { get; set; } = [];

    /// <summary>The sheet's data rows, in reading order.</summary>
    public List<ConversationDocumentSheetRow> Rows { get; set; } = [];
}
