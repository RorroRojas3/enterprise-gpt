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
            // A store default so a column added later backfills existing rows to visible rather than
            // hiding the whole catalog at once. ValueGeneratedNever because EF's seed differ skips
            // store-generated columns, and would drop this one from HasData with no error at all.
            builder.Property(x => x.IsUserSelectable)
                .HasDefaultValue(true)
                .ValueGeneratedNever();

            var dateCreated = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var userId = Guid.Parse("5f7ab694-1b6c-4b19-badd-c82b65e794cf");
            builder.HasData(
                new Model
                {
                    Id = new("c36e22ed-262a-47a1-b2ba-06a38355ae0f"),
                    ProviderId = Providers.AzureOpenAI,
                    Name = "RR GPT 5.6 Luna",
                    // The pinned document summarizer. Its window and output cap are asserted here
                    // rather than left at zero because the summarization engine reads both from
                    // this row at the moment of use, and hidden from the picker because it is a
                    // purpose-built deployment nobody should be able to hold a conversation with.
                    DeploymentName = "rr-gpt5.6-luna",
                    ContextWindowSize = 1_000_000m,
                    MaxOutputTokens = 16_384m,
                    IsUserSelectable = false,
                    IsToolEnabled = true,
                    Description = "OpenAI's GPT-5.6 Luna model.",
                    DateCreated = dateCreated,
                    DateModified = dateCreated,
                    CreatedById = userId,
                    ModifiedById = userId
                },
                new Model
                {
                    Id = new("8f2b4d16-9c05-4a3e-8f7a-1d6a9c2b5e04"),
                    ProviderId = Providers.AzureOpenAI,
                    Name = "RR GPT 5.6 Luna (File Agent)",
                    // The pinned File Agent. Its own row rather than the summarizer's so the two can be
                    // repointed independently, and on Azure OpenAI because the hosted code interpreter
                    // rides that provider's Responses route and no other. Hidden from the picker: it is
                    // a purpose-built deployment nobody should be able to hold a conversation with.
                    DeploymentName = "rr-gpt5.6-luna",
                    ContextWindowSize = 1_000_000m,
                    MaxOutputTokens = 16_384m,
                    IsUserSelectable = false,
                    IsToolEnabled = true,
                    Description = "Runs the File Agent's sandbox turns.",
                    DateCreated = dateCreated,
                    DateModified = dateCreated,
                    CreatedById = userId,
                    ModifiedById = userId
                }
            );
        }
    }
}
