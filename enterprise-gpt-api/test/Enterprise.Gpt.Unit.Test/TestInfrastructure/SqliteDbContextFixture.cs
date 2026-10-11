using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Repository.Sql.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// An isolated in-memory SQLite database per test, created from the <see cref="EnterpriseGptDbContext"/>
/// model and seeds. The connection stays open because the database lives only as long as it does.
/// </summary>
/// <remarks>
/// Contexts handed to services carry the production interceptors; <see cref="CreateContext"/> is bare, so
/// arrange and assert code writes and reads exactly the values it states.
/// </remarks>
public sealed class SqliteDbContextFixture : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<EnterpriseGptDbContext> _options;
    private readonly DbContextOptions<EnterpriseGptDbContext> _serviceOptions;
    private readonly ServiceProvider _provider;
    private readonly List<IDisposable> _extra = [];

    public SqliteDbContextFixture(TimeProvider? timeProvider = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<EnterpriseGptDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, SqliteRowVersionModelCustomizer>()
            .Options;

        var time = timeProvider ?? TimeProvider.System;
        _serviceOptions = new DbContextOptionsBuilder<EnterpriseGptDbContext>(_options)
            .AddInterceptors(new SoftDeleteInterceptor(time), new AuditTimestampInterceptor(time))
            .ConfigureWarnings(warnings => warnings.Throw(
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning))
            .Options;

        Context = new EnterpriseGptDbContext(_serviceOptions);
        Context.Database.EnsureCreated();

        // A container whose scopes hand out fresh contexts on the same database, for a service that
        // deliberately writes on a unit of work of its own rather than the caller's.
        var services = new ServiceCollection();
        services.AddScoped(_ => new EnterpriseGptDbContext(_serviceOptions));
        _provider = services.BuildServiceProvider();
    }

    /// <summary>The context handed to the service under test, with the production interceptors.</summary>
    public EnterpriseGptDbContext Context { get; }

    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>A bare context on the same database, for arranging and asserting.</summary>
    public EnterpriseGptDbContext CreateContext()
    {
        return new EnterpriseGptDbContext(_options);
    }

    /// <summary>A service context with one more interceptor after the production ones, to simulate a fault.</summary>
    public EnterpriseGptDbContext CreateServiceContext(IInterceptor extra)
    {
        var ctx = new EnterpriseGptDbContext(new DbContextOptionsBuilder<EnterpriseGptDbContext>(_serviceOptions)
            .AddInterceptors(extra)
            .Options);
        _extra.Add(ctx);
        return ctx;
    }

    /// <summary>A scope factory whose contexts carry one more interceptor after the production ones.</summary>
    public IServiceScopeFactory CreateScopeFactory(IInterceptor extra)
    {
        var options = new DbContextOptionsBuilder<EnterpriseGptDbContext>(_serviceOptions).AddInterceptors(extra).Options;
        var provider = new ServiceCollection()
            .AddScoped(_ => new EnterpriseGptDbContext(options))
            .BuildServiceProvider();
        _extra.Add(provider);
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose()
    {
        // Ahead of the connection: the scoped contexts it created share it, and EF does not close a
        // connection it was handed rather than opened.
        foreach (var disposable in _extra)
        {
            disposable.Dispose();
        }

        _provider.Dispose();
        Context.Dispose();
        _connection.Dispose();
    }
}
