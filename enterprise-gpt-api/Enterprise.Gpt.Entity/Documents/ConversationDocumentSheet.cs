using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

public class ConversationDocumentSheet : BaseDocumentSheet<Guid>
{
    public Guid ConversationDocumentId { get; set; }

    public ConversationDocument ConversationDocument { get; set; } = null!;

    public List<ConversationDocumentSheetColumn> Columns { get; set; } = [];

    public List<ConversationDocumentSheetRow> Rows { get; set; } = [];
}
