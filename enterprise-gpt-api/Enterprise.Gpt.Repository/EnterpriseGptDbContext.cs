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

        public DbSet<ConversationDocument> ConversationDocuments { get; set; }

        public DbSet<ConversationDocumentChunk> ConversationDocumentChunks { get; set; }

        public DbSet<User> Users { get; set; }

        public DbSet<UserPermission> UserPermissions { get; set; }

        public DbSet<Permission> Permissions { get; set; }

        public DbSet<McpServer> McpServers { get; set; }
        #endregion

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new UserConfiguration());
            modelBuilder.ApplyConfiguration(new UserPermissionConfiguration());
            modelBuilder.ApplyConfiguration(new PermissionConfiguration());
            modelBuilder.ApplyConfiguration(new McpServerConfiguration());
            modelBuilder.ApplyConfiguration(new ProviderConfiguration());
            modelBuilder.ApplyConfiguration(new ModelConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationDocumentConfiguration());
            modelBuilder.ApplyConfiguration(new ConversationDocumentChunkConfiguration());

            // Configure global delete behavior
            foreach (var relationship in modelBuilder.Model.GetEntityTypes()
                .SelectMany(e => e.GetForeignKeys()))
            {
                relationship.DeleteBehavior = DeleteBehavior.NoAction;
            }
        }
    }
}
