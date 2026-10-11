using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

public class ConversationDocumentSheetColumn : BaseDocumentSheetColumn<Guid>
{
    public Guid ConversationDocumentSheetId { get; set; }

    public ConversationDocumentSheet ConversationDocumentSheet { get; set; } = null!;
}
