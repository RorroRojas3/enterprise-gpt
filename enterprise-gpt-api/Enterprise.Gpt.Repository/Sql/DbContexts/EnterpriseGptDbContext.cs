using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Entity.McpServers;
using Enterprise.Gpt.Entity.Permissions;
using Enterprise.Gpt.Entity.Projects;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Entity.Users;
using Enterprise.Gpt.Repository.Sql.Configurations.ChatModels;
using Enterprise.Gpt.Repository.Sql.Configurations.Conversations;
using Enterprise.Gpt.Repository.Sql.Configurations.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.McpServers;
using Enterprise.Gpt.Repository.Sql.Configurations.Permissions;
using Enterprise.Gpt.Repository.Sql.Configurations.Projects;
using Enterprise.Gpt.Repository.Sql.Configurations.Usage;
using Enterprise.Gpt.Repository.Sql.Configurations.Users;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Enterprise.Gpt.Repository.Sql.DbContexts
{
    public class EnterpriseGptDbContext(DbContextOptions<EnterpriseGptDbContext> options)
        : DbContext(options), IDataProtectionKeyContext
    {
        #region DbSets
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
        /// The Data Protection key ring, owned by the framework rather than by this application:
        /// no configuration maps it and nothing here reads it. It is a <c>DbSet</c> only because
        /// <see cref="IDataProtectionKeyContext"/> requires one.
        /// </summary>
        public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
        #endregion

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new UserConfiguration());
            modelBuilder.ApplyConfiguration(new UserPermissionConfiguration());
            modelBuilder.ApplyConfiguration(new PermissionConfiguration());
            modelBuilder.ApplyConfiguration(new McpServerConfiguration());
            modelBuilder.ApplyConfiguration(new UserMcpCredentialConfiguration());
            modelBuilder.ApplyConfiguration(new ProviderConfiguration());
            modelBuilder.ApplyConfiguration(new ModelConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationUsageConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationUsageMcpServerConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationUsageToolCallConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationUsageTurnConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationDocumentConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationDocumentChunkConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationDocumentSummaryConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationDocumentSheetConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationDocumentSheetColumnConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationDocumentSheetRowConfiguration());
            modelBuilder.ApplyConfiguration(new ProjectConfiguration());
            modelBuilder.ApplyConfiguration(new ProjectDocumentConfiguration());
            modelBuilder.ApplyConfiguration(new ProjectDocumentChunkConfiguration());
            modelBuilder.ApplyConfiguration(new ProjectDocumentSummaryConfiguration());
            modelBuilder.ApplyConfiguration(new ProjectDocumentSheetConfiguration());
            modelBuilder.ApplyConfiguration(new ProjectDocumentSheetColumnConfiguration());
            modelBuilder.ApplyConfiguration(new ProjectDocumentSheetRowConfiguration());
            modelBuilder.ApplyConfiguration(new MessageFeedbackConfiguration());

            // Configure global delete behavior
            foreach (var relationship in modelBuilder.Model.GetEntityTypes()
                .SelectMany(e => e.GetForeignKeys()))
            {
                relationship.DeleteBehavior = DeleteBehavior.NoAction;
            }
        }
    }
}
