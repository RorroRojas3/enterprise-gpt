using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Enterprise.Gpt.Repository;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Provides an isolated in-memory SQLite database per test, built from the
/// <see cref="EnterpriseGptDbContext"/> model (including its HasData seeds) via
/// <c>EnsureCreated()</c>. The connection stays open for the fixture's lifetime because an
/// in-memory SQLite database only lives as long as its connection.
/// </summary>
public sealed class SqliteDbContextFixture : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<EnterpriseGptDbContext> _options;

    public SqliteDbContextFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<EnterpriseGptDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, SqliteRowVersionModelCustomizer>()
            .Options;

        Context = new EnterpriseGptDbContext(_options);
        Context.Database.EnsureCreated();
    }

    /// <summary>
    /// Gets the context handed to the service under test.
    /// </summary>
    public EnterpriseGptDbContext Context { get; }

    /// <summary>
    /// Creates a fresh context on the same database so arrangement and assertions read
    /// persisted state rather than entities tracked by <see cref="Context"/>.
    /// </summary>
    /// <returns>A new context sharing the fixture's connection.</returns>
    public EnterpriseGptDbContext CreateContext()
    {
        return new EnterpriseGptDbContext(_options);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
