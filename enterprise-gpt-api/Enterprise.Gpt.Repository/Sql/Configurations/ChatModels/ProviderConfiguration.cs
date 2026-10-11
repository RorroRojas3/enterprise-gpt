using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Constants.ChatModels;
using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.ChatModels;

public class ProviderConfiguration : BaseCreatedEntityConfiguration<Provider, Guid>
{
    /// <inheritdoc />
    /// <remarks>Usage, history and audit columns reach this through required navigations, so a retired row must stay visible.</remarks>
    protected override bool IsSoftDeleteFiltered => false;

    protected override void ConfigureEntity(EntityTypeBuilder<Provider> builder)
    {
        builder.ToTable(nameof(Provider), "Core.Ref");

        builder.Property(x => x.Name).HasMaxLength(ProviderLimits.NameMaxLength);

        var date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        builder.HasData(
            new Provider
            {
                Id = Providers.AzureOpenAI,
                Name = nameof(Providers.AzureOpenAI),
                DateCreated = date,
            },
            new Provider
            {
                Id = Providers.AzureAIFoundry,
                Name = nameof(Providers.AzureAIFoundry),
                DateCreated = date,
            },
            new Provider
            {
                Id = Providers.AmazonBedrock,
                Name = nameof(Providers.AmazonBedrock),
                DateCreated = date,
            },
            new Provider
            {
                Id = Providers.Anthropic,
                Name = nameof(Providers.Anthropic),
                DateCreated = date,
            }
        );
    }
}
