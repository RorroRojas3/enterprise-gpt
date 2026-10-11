using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Entity.McpServers;
using Enterprise.Gpt.Entity.Permissions;
using Enterprise.Gpt.Entity.Projects;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Entity.Users;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Enterprise.Gpt.Repository.Sql.DbContexts;

public class EnterpriseGptDbContext(DbContextOptions<EnterpriseGptDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Provider> Providers => Set<Provider>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Model> Models => Set<Model>();

    public DbSet<ConversationUsage> ConversationUsage => Set<ConversationUsage>();

    public DbSet<ConversationUsageMcpServer> ConversationUsageMcpServers => Set<ConversationUsageMcpServer>();

    public DbSet<ConversationUsageToolCall> ConversationUsageToolCalls => Set<ConversationUsageToolCall>();

    public DbSet<ConversationUsageTurn> ConversationUsageTurns => Set<ConversationUsageTurn>();

    public DbSet<ConversationDocument> ConversationDocuments => Set<ConversationDocument>();

    public DbSet<ConversationDocumentChunk> ConversationDocumentChunks => Set<ConversationDocumentChunk>();

    public DbSet<ConversationDocumentSummary> ConversationDocumentSummaries => Set<ConversationDocumentSummary>();

    public DbSet<ConversationDocumentSheet> ConversationDocumentSheets => Set<ConversationDocumentSheet>();

    public DbSet<ConversationDocumentSheetColumn> ConversationDocumentSheetColumns => Set<ConversationDocumentSheetColumn>();

    public DbSet<ConversationDocumentSheetRow> ConversationDocumentSheetRows => Set<ConversationDocumentSheetRow>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectDocument> ProjectDocuments => Set<ProjectDocument>();

    public DbSet<ProjectDocumentChunk> ProjectDocumentChunks => Set<ProjectDocumentChunk>();

    public DbSet<ProjectDocumentSummary> ProjectDocumentSummaries => Set<ProjectDocumentSummary>();

    public DbSet<ProjectDocumentSheet> ProjectDocumentSheets => Set<ProjectDocumentSheet>();

    public DbSet<ProjectDocumentSheetColumn> ProjectDocumentSheetColumns => Set<ProjectDocumentSheetColumn>();

    public DbSet<ProjectDocumentSheetRow> ProjectDocumentSheetRows => Set<ProjectDocumentSheetRow>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<McpServer> McpServers => Set<McpServer>();

    public DbSet<UserMcpCredential> UserMcpCredentials => Set<UserMcpCredential>();

    public DbSet<MessageFeedback> MessageFeedback => Set<MessageFeedback>();

    /// <summary>
    /// The framework-owned Data Protection key ring; <see cref="IDataProtectionKeyContext"/> requires it.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EnterpriseGptDbContext).Assembly);

        // NoAction on every key: rows are soft-deleted, and SQL Server rejects the multiple cascade paths
        // this graph would otherwise create.
        foreach (var relationship in modelBuilder.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys()))
        {
            relationship.DeleteBehavior = DeleteBehavior.NoAction;
        }
    }
}
