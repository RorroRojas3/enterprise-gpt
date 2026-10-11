using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Constants.ChatModels;
using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.ChatModels;

public class ModelConfiguration : BaseModifiedByEntityConfiguration<Model, Guid>
{
    /// <inheritdoc />
    /// <remarks>Usage, history and audit columns reach this through required navigations, so a retired row must stay visible.</remarks>
    protected override bool IsSoftDeleteFiltered => false;

    protected override void ConfigureEntity(EntityTypeBuilder<Model> builder)
    {
        builder.ToTable(nameof(Model), "Core.Ref");

        builder.Property(x => x.Name).HasMaxLength(ModelLimits.NameMaxLength);
        builder.Property(x => x.DeploymentName).HasMaxLength(ModelLimits.DeploymentNameMaxLength);
        builder.Property(x => x.Description).HasMaxLength(ModelLimits.DescriptionMaxLength);
        builder.Property(x => x.ContextWindowSize).HasColumnType("decimal(18, 2)");
        builder.Property(x => x.MaxOutputTokens).HasColumnType("decimal(18, 2)");
        builder.Property(x => x.InputPricePerMillionTokens).HasColumnType("decimal(18, 6)");
        builder.Property(x => x.OutputPricePerMillionTokens).HasColumnType("decimal(18, 6)");

        builder.HasOne(x => x.Provider)
            .WithMany()
            .HasForeignKey(x => x.ProviderId);

        // The store default backfills existing rows as visible. ValueGeneratedNever because EF's seed differ
        // skips store-generated columns and would silently drop this one from HasData.
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
                // The pinned document summarizer, hidden from the picker. The summarization engine reads
                // its window and output cap from this row at the moment of use.
                DeploymentName = "rr-gpt5.6-luna",
                ContextWindowSize = 1_000_000m,
                // A full summary plus reasoning, which shares this cap; reserved out of the window.
                MaxOutputTokens = 32_768m,
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
                // The pinned File Agent, hidden from the picker. Its own row so it can be repointed apart from
                // the summarizer; Azure OpenAI because the hosted code interpreter needs its Responses route.
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
