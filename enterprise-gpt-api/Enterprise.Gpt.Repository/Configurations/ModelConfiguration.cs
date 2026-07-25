using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Repository.Configurations
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
                    ProviderId = Providers.AzureOpenAI,
                    Name = "rr-gpt-5.6-luna",
                    IsToolEnabled = true,
                    Description = "OpenAI's GPT-5.6 Luna model.",
                    DateCreated = dateCreated,
                    DateModified = dateCreated,
                    CreatedById = userId,
                    ModifiedById = userId
                }
            );
        }
    }
}
