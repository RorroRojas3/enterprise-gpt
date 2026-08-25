using Enterprise.Gpt.Repository.Migrations;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Summarization;

/// <summary>
/// Guards the migration path, which no other test reaches: both suites build their schema with
/// <c>EnsureCreated</c>, so every assertion about the corrected summarizer row elsewhere proves
/// only the freshly-created half.
/// </summary>
/// <remarks>
/// The failure this exists for is silent. EF's seed-data differ skips store-generated columns, so
/// leaving <c>IsUserSelectable</c> at its convention-assigned <c>ValueGeneratedOnAdd</c> makes the
/// scaffolder emit a shorter <c>UpdateData</c> with no error at all — and a migrated database then
/// keeps the summarizer visible while a new one hides it.
/// <para>
/// Asserted against <c>ModelConfiguration</c>'s own <c>HasData</c> rather than against literals, so
/// it also catches the drift that bites in practice: the seed edited and the migration not
/// regenerated. Reading the operation instead of running it keeps the guard in the unit suite,
/// where it costs no container.
/// </para>
/// </remarks>
public sealed class CorrectSummarizerModelSeedTests : IDisposable
{
    private static readonly Guid SummarizerId = new("c36e22ed-262a-47a1-b2ba-06a38355ae0f");

    private readonly SqliteDbContextFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    /// <summary>The seeded row as <c>ModelConfiguration.HasData</c> declares it.</summary>
    /// <remarks>
    /// Read off the design-time model, not <c>DbContext.Model</c>: the runtime model is
    /// read-optimized and drops seed data, which is the same model the scaffolder diffs against.
    /// </remarks>
    private IDictionary<string, object?> SeedRow()
    {
        return _fixture.Context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(Entity.Model))!
            .GetSeedData()
            .Single(row => (Guid)row["Id"]! == SummarizerId);
    }

    [Fact]
    public void Up_CorrectsEveryColumnTheSeedDeclares()
    {
        var seed = SeedRow();
        var migration = new CorrectSummarizerModelSeed();

        var operation = Assert.IsType<UpdateDataOperation>(Assert.Single(migration.UpOperations));

        Assert.Equal("Model", operation.Table);
        Assert.Equal("Core.Ref", operation.Schema);
        Assert.Equal(SummarizerId, Assert.Single(operation.KeyValues!));

        // The differ silently omits a column it considers store-generated, which is the whole
        // failure mode — so the guard is that all four are present and each carries the seed's
        // own value. Compared positionally, because Columns and Values are parallel arrays.
        Assert.Equal(
            ["ContextWindowSize", "DeploymentName", "IsUserSelectable", "MaxOutputTokens"],
            operation.Columns);
        for (var index = 0; index < operation.Columns.Length; index++)
        {
            Assert.Equal(seed[operation.Columns[index]], operation.Values[0, index]);
        }
    }

    [Fact]
    public void Down_RestoresTheOriginalSeededValues()
    {
        var migration = new CorrectSummarizerModelSeed();

        var operation = Assert.IsType<UpdateDataOperation>(Assert.Single(migration.DownOperations));

        Assert.Equal(
            ["ContextWindowSize", "DeploymentName", "IsUserSelectable", "MaxOutputTokens"],
            operation.Columns);
        Assert.Equal([0m, "rr-gpt-5.6-luna", true, 0m], operation.Values!.Cast<object>());
    }
}
