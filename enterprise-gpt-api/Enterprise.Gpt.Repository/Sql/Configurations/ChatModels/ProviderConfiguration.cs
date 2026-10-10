using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Entity.ChatModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.ChatModels
{
    public class ProviderConfiguration : IEntityTypeConfiguration<Provider>
    {
        public void Configure(EntityTypeBuilder<Provider> builder)
        {
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
}
