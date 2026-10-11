using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Persistence;

/// <summary>
/// The rest of the suite builds its schema with <c>EnsureCreated</c>, so this is the only test that runs the
/// migrations a deployment applies at startup.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MigrationIntegrationTests(IntegrationTestFixture fixture)
{
    private readonly IntegrationTestFixture _fixture = fixture;

    [Fact]
    public async Task Migrate_EmptyDatabase_AppliesEveryMigrationAndDropsTheLedgerRowVersions()
    {
        var connectionString = new SqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            InitialCatalog = $"MigrateFromEmpty_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<EnterpriseGptDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(170))
            .Options;
        await using var ctx = new EnterpriseGptDbContext(options);

        try
        {
            await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
            Assert.True(await ctx.Models.AnyAsync(TestContext.Current.CancellationToken));

            var tablesWithVersion = await ctx.Database
                .SqlQuery<string>($"""
                    SELECT TABLE_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE COLUMN_NAME = 'Version'
                      AND TABLE_NAME IN ('Provider', 'ConversationUsage', 'ConversationUsageMcpServer',
                                         'ConversationUsageToolCall', 'ConversationUsageTurn', 'Conversation')
                    """)
                .ToListAsync(TestContext.Current.CancellationToken);

            // The ledger tables lost theirs; an edited table keeps its concurrency token.
            Assert.Equal(["Conversation"], tablesWithVersion);
        }
        finally
        {
            await ctx.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        }
    }
}
