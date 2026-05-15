using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RR.AI_Chat.Dto.Enums;
using RR.AI_Chat.Entity;

namespace RR.AI_Chat.Repository.Configurations
{
    public class ModelConfiguration : IEntityTypeConfiguration<Model>
    {
        public void Configure(EntityTypeBuilder<Model> builder)
        {
            var dateCreated = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var userId = Guid.Parse("5f7ab694-1b6c-4b19-badd-c82b65e794cf");
            builder.HasData(
                new Model
                {
                    Id = new("c36e22ed-262a-47a1-b2ba-06a38355ae0f"),
                    Name = "gpt-5-mini",
                    IsToolEnabled = true,
                    Description = "A mini version of GPT-5, designed for basic tasks and quick responses.",
                    DateCreated = dateCreated,
                    DateModified = dateCreated,
                    CreatedById = userId,
                    ModifiedById = userId
                },
                new Model
                {
                    Id = new("fd01b615-1e9f-46af-957f-e4eaeff02766"),
                    Name = "gpt-5-nano",
                    IsToolEnabled = true,
                    Description = "A nano version of GPT-5, optimized for lightweight tasks and minimal resource usage.",
                    DateCreated = dateCreated,
                    DateModified = dateCreated,
                    CreatedById = userId,
                    ModifiedById = userId
                },
                new Model
                {
                    Id = new("0b3948f5-70df-4697-a033-ae70971e1796"),
                    Name = "gpt-5-chat",
                    IsToolEnabled = true,
                    Description = "A chat version of GPT-5, optimized for conversational tasks and interactive applications.",
                    DateCreated = dateCreated,
                    DateModified = dateCreated,
                    ModifiedById = userId,
                    CreatedById = userId
                }
            );
        }
    }
}
