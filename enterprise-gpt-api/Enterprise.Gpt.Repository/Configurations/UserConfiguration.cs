using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RR.AI_Chat.Entity;

namespace RR.AI_Chat.Repository.Configurations
{
    public sealed class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            builder.HasData(
                new User
                {
                    Id = Guid.Parse("5f7ab694-1b6c-4b19-badd-c82b65e794cf"),
                    FirstName = "Enterprise",
                    LastName = "AI",
                    Email = "Enterprise.AI@example.com",
                    DateCreated = date,
                    DateModified = date,
                }
            );
        }
    }
}
