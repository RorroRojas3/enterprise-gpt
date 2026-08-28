using Microsoft.EntityFrameworkCore;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository.Configurations;

namespace Enterprise.Gpt.Repository
{
    public class EnterpriseGptDbContext(DbContextOptions<EnterpriseGptDbContext> options) : DbContext(options)
    {
        #region DbSets
        public DbSet<Provider> Providers { get; set; }

        public DbSet<Conversation> Conversations { get; set; }

        public DbSet<Model> Models { get; set; }

        public DbSet<ConversationUsage> ConversationUsage { get; set; }

        public DbSet<ConversationUsageMcpServer> ConversationUsageMcpServers { get; set; }

        public DbSet<ConversationUsageToolCall> ConversationUsageToolCalls { get; set; }

        public DbSet<ConversationUsageTurn> ConversationUsageTurns { get; set; }

        public DbSet<ConversationDocument> ConversationDocuments { get; set; }

        public DbSet<ConversationDocumentChunk> ConversationDocumentChunks { get; set; }

        public DbSet<ConversationDocumentSummary> ConversationDocumentSummaries { get; set; }

        public DbSet<ConversationDocumentSheet> ConversationDocumentSheets { get; set; }

        public DbSet<ConversationDocumentSheetColumn> ConversationDocumentSheetColumns { get; set; }

        public DbSet<ConversationDocumentSheetRow> ConversationDocumentSheetRows { get; set; }

        public DbSet<Project> Projects { get; set; }

        public DbSet<ProjectDocument> ProjectDocuments { get; set; }

        public DbSet<ProjectDocumentChunk> ProjectDocumentChunks { get; set; }

        public DbSet<ProjectDocumentSummary> ProjectDocumentSummaries { get; set; }

        public DbSet<ProjectDocumentSheet> ProjectDocumentSheets { get; set; }

        public DbSet<ProjectDocumentSheetColumn> ProjectDocumentSheetColumns { get; set; }

        public DbSet<ProjectDocumentSheetRow> ProjectDocumentSheetRows { get; set; }

        public DbSet<User> Users { get; set; }

        public DbSet<UserPermission> UserPermissions { get; set; }

        public DbSet<Permission> Permissions { get; set; }

        public DbSet<McpServer> McpServers { get; set; }

        public DbSet<MessageFeedback> MessageFeedback { get; set; }
        #endregion

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new UserConfiguration());
            modelBuilder.ApplyConfiguration(new UserPermissionConfiguration());
            modelBuilder.ApplyConfiguration(new PermissionConfiguration());
            modelBuilder.ApplyConfiguration(new McpServerConfiguration());
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
