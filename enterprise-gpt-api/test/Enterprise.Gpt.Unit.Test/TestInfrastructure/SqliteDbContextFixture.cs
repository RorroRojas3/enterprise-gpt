using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly ServiceProvider _provider;

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

        // A container whose scopes hand out fresh contexts on the same database, for a service that
        // deliberately writes on a unit of work of its own rather than the caller's.
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContext());
        _provider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Gets the context handed to the service under test.
    /// </summary>
    public EnterpriseGptDbContext Context { get; }

    /// <summary>
    /// Gets a scope factory whose scopes resolve fresh contexts on the same database, for services
    /// that take <see cref="IServiceScopeFactory"/> to keep their writes off the caller's context.
    /// </summary>
    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

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
        // Ahead of the connection: the scoped contexts it created share it, and EF does not close a
        // connection it was handed rather than opened.
        _provider.Dispose();
        Context.Dispose();
        _connection.Dispose();
    }
}
