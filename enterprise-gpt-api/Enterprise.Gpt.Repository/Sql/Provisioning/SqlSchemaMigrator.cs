using Enterprise.Gpt.Repository.Sql.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Repository.Sql.Provisioning;

/// <summary>
/// Brings the application database to the latest migration.
/// </summary>
public sealed partial class SqlSchemaMigrator(EnterpriseGptDbContext ctx, ILogger<SqlSchemaMigrator> logger)
{
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly ILogger<SqlSchemaMigrator> _logger = logger;

    /// <summary>
    /// Creates the database when it does not exist and applies every pending migration.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await _ctx.Database.MigrateAsync(cancellationToken);

        LogMigrated(_logger);
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied successfully")]
    private static partial void LogMigrated(ILogger logger);

    #endregion
}
