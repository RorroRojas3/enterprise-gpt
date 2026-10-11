using Enterprise.Gpt.Repository.Sql.Constants;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Repository.Sql;

/// <summary>
/// Pins the shape of the soft-delete filter on the SQL Server model, which no query test would notice drifting.
/// </summary>
public class SoftDeleteModelTests
{
    private static readonly IModel _model = CreateModel();

    /// <summary>
    /// Required navigations into a filtered principal, reviewed because EF warns only for unfiltered
    /// dependents: each dependent is deactivated with its principal or never read without it.
    /// </summary>
    private static readonly string[] _reviewedRequiredNavigations =
    [
        // Document families: every deactivation cascade deactivates children before their parent.
        "ConversationDocument.Conversation -> Conversation",
        "ConversationDocumentChunk.ConversationDocument -> ConversationDocument",
        "ConversationDocumentSheet.ConversationDocument -> ConversationDocument",
        "ConversationDocumentSheetColumn.ConversationDocumentSheet -> ConversationDocumentSheet",
        "ConversationDocumentSheetRow.ConversationDocumentSheet -> ConversationDocumentSheet",
        "ConversationDocumentSummary.ConversationDocument -> ConversationDocument",
        "ProjectDocument.Project -> Project",
        "ProjectDocumentChunk.ProjectDocument -> ProjectDocument",
        "ProjectDocumentSheet.ProjectDocument -> ProjectDocument",
        "ProjectDocumentSheetColumn.ProjectDocumentSheet -> ProjectDocumentSheet",
        "ProjectDocumentSheetRow.ProjectDocumentSheet -> ProjectDocumentSheet",
        "ProjectDocumentSummary.ProjectDocument -> ProjectDocument",

        // The usage ledger outlives its conversation and is read by its own columns, never through this
        // navigation; its child rows hang off usage rows, which are never deactivated.
        "ConversationUsage.Conversation -> Conversation",
        "ConversationUsageMcpServer.ConversationUsage -> ConversationUsage",
        "ConversationUsageToolCall.ConversationUsage -> ConversationUsage",
        "ConversationUsageTurn.ConversationUsage -> ConversationUsage",

        // Feedback is only read within a live conversation.
        "MessageFeedback.Conversation -> Conversation",

        // Deactivating a permission deactivates its grants in the same save.
        "UserPermission.Permission -> Permission"
    ];

    [Fact]
    public void RequiredNavigationsIntoFilteredPrincipals_AreTheReviewedSet()
    {
        var actual = _model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(fk => fk.IsRequired && IsFiltered(fk.PrincipalEntityType))
            .Select(fk => $"{fk.DeclaringEntityType.ClrType.Name}.{fk.DependentToPrincipal?.Name ?? fk.Properties[0].Name} -> {fk.PrincipalEntityType.ClrType.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(_reviewedRequiredNavigations.Order(StringComparer.Ordinal), actual);
    }

    /// <summary>
    /// History and reports reach these through required navigations, so filtering one would drop the history row.
    /// </summary>
    [Fact]
    public void UnfilteredEntities_AreTheHistoryTargetsAndTheKeyRing()
    {
        var unfiltered = _model.GetEntityTypes()
            .Where(entity => !IsFiltered(entity))
            .Select(entity => entity.ClrType.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["DataProtectionKey", "McpServer", "Model", "Provider", "User"], unfiltered);
    }

    [Fact]
    public void Model_HasNoChangesMissingFromTheMigrations()
    {
        using var ctx = CreateContext();

        Assert.False(ctx.Database.HasPendingModelChanges());
    }

    #region Private methods

    private static bool IsFiltered(IReadOnlyEntityType entity) =>
        entity.FindDeclaredQueryFilter(QueryFilterNames.SoftDelete) is not null;

    // SQL Server provider and no connection: building the model and diffing it against the migrations open none.
    private static EnterpriseGptDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<EnterpriseGptDbContext>()
            .UseSqlServer("Server=unused", sql => sql.UseCompatibilityLevel(170))
            .Options);

    private static IModel CreateModel()
    {
        using var ctx = CreateContext();
        return ctx.Model;
    }

    #endregion
}
