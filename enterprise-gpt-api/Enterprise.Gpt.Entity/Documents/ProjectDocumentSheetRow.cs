using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// A data row of a sheet belonging to a project document.
/// </summary>
public class ProjectDocumentSheetRow : BaseDocumentSheetRow<Guid>
{
    /// <summary>The sheet this row belongs to.</summary>
    public Guid ProjectDocumentSheetId { get; set; }

    public ProjectDocumentSheet ProjectDocumentSheet { get; set; } = null!;
}
