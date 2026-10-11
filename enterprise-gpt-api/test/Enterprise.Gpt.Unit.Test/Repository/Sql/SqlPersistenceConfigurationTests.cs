using Enterprise.Gpt.Repository.Sql;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Repository.Sql.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Repository.Sql;

/// <summary>
/// Pins what the shipped registration attaches to every context, which the SQLite fixture only imitates.
/// </summary>
public sealed class SqlPersistenceConfigurationTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly CoreOptionsExtension _options;

    public SqlPersistenceConfigurationTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Server=unused" })
            .Build();
        _provider = new ServiceCollection().AddEnterpriseGptSqlPersistence(configuration).BuildServiceProvider();
        _scope = _provider.CreateScope();

        var ctx = _scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();
        _options = ctx.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!;
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    /// <summary>
    /// Soft delete first: the entry it flips to Modified must then be stamped by the audit interceptor.
    /// </summary>
    [Fact]
    public void AddEnterpriseGptSqlPersistence_Interceptors_RunSoftDeleteBeforeAudit()
    {
        Assert.Collection(
            _options.Interceptors!,
            interceptor => Assert.IsType<SoftDeleteInterceptor>(interceptor),
            interceptor => Assert.IsType<AuditTimestampInterceptor>(interceptor));
    }

    [Fact]
    public void AddEnterpriseGptSqlPersistence_RequiredNavigationIntoAFilteredPrincipal_IsAnError()
    {
        var behavior = _options.WarningsConfiguration.GetBehavior(
            CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning);

        Assert.Equal(WarningBehavior.Throw, behavior);
    }
}
