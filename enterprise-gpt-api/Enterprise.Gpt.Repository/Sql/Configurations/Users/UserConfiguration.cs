using Enterprise.Gpt.Common.Constants.Users;
using Enterprise.Gpt.Entity.Users;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Users
{
    public sealed class UserConfiguration : BaseModifiedEntityConfiguration<User, Guid>
    {
        /// <inheritdoc />
        /// <remarks>Usage, history and audit columns reach this through required navigations, so a retired row must stay visible.</remarks>
        protected override bool IsSoftDeleteFiltered => false;

        protected override void ConfigureEntity(EntityTypeBuilder<User> builder)
        {
            builder.ToTable(nameof(User), "Core");

            builder.Property(x => x.FirstName).HasMaxLength(UserLimits.FirstNameMaxLength);
            builder.Property(x => x.LastName).HasMaxLength(UserLimits.LastNameMaxLength);
            builder.Property(x => x.Email).HasMaxLength(UserLimits.EmailMaxLength);
            builder.Ignore(x => x.FullName);

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
