using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Entity.Users;

namespace Enterprise.Gpt.Entity.Projects;

/// <summary>
/// A user-owned container that groups conversations and holds a document set and standing
/// instructions shared by every conversation inside it.
/// </summary>
/// <remarks>
/// Ownership is the whole authorization model: a project, its documents and its conversations
/// all carry the same <see cref="UserId"/>, and every query filters on it.
/// </remarks>
public class Project : BaseModifiedEntity<Guid>
{
    /// <summary>
    /// The Entra object id of the owner. Every read and write is scoped to it.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The project name, unique among the owner's active projects.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Optional free-text description. Never sent to the model.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether the owner has marked this project as a favourite.
    /// </summary>
    /// <remarks>
    /// A plain column rather than a join table, because a project has exactly one owner: the
    /// per-user favourite and the per-row flag are the same fact. Set through its own route so a
    /// full-representation <c>PUT</c> that omits it cannot clear it.
    /// </remarks>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Standing instructions prepended to the system prompt of every conversation in this project.
    /// </summary>
    /// <remarks>
    /// Read at each turn rather than snapshotted into the conversation transcript, so editing them
    /// affects later turns and moving a conversation between projects behaves as expected.
    /// </remarks>
    public string? Instructions { get; set; }

    public User User { get; set; } = null!;

    /// <summary>
    /// The conversations in this project. Released rather than deleted when the project is
    /// deactivated — see the deactivation cascade in <c>IProjectService</c>.
    /// </summary>
    public List<Conversation> Conversations { get; set; } = [];

    /// <summary>
    /// The documents shared by every conversation in this project. Distinct from the documents
    /// uploaded into an individual conversation, which stay private to it.
    /// </summary>
    public List<ProjectDocument> Documents { get; set; } = [];
}
