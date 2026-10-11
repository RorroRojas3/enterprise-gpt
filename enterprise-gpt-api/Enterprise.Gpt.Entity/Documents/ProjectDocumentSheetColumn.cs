using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// A column of a sheet belonging to a project document.
/// </summary>
public class ProjectDocumentSheetColumn : BaseDocumentSheetColumn<Guid>
{
    /// <summary>The sheet this column belongs to.</summary>
    public Guid ProjectDocumentSheetId { get; set; }

    public ProjectDocumentSheet ProjectDocumentSheet { get; set; } = null!;
}
