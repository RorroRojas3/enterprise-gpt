using Microsoft.EntityFrameworkCore;
using RR.AI_Chat.Entity;
using RR.AI_Chat.Repository.Configurations;

namespace RR.AI_Chat.Repository
{
    public class AIChatDbContext(DbContextOptions<AIChatDbContext> options) : DbContext(options)
    {
        #region DbSets
        public DbSet<AIService> AIServices { get; set; }

        public DbSet<Conversation> Conversations { get; set; }

        public DbSet<Model> Models { get; set; }

        public DbSet<ConversationDocument> ConversationDocuments { get; set; }

        public DbSet<ConversationDocumentPage> ConversationDocumentPages { get; set; }

        public DbSet<User> Users { get; set; }

        public DbSet<UserPermission> UserPermissions { get; set; }
        #endregion

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new UserConfiguration());
            modelBuilder.ApplyConfiguration(new UserPermissionConfiguration());
            modelBuilder.ApplyConfiguration(new AIServiceConfiguration());
            modelBuilder.ApplyConfiguration(new ModelConfiguration());

            // Configure global delete behavior
            foreach (var relationship in modelBuilder.Model.GetEntityTypes()
                .SelectMany(e => e.GetForeignKeys()))
            {
                relationship.DeleteBehavior = DeleteBehavior.NoAction;
            }
        }
    }
}
