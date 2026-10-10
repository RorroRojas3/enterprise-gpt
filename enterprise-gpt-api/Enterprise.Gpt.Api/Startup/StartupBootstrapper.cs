using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Repository.Cosmos.Provisioning;
using Enterprise.Gpt.Repository.Sql.Provisioning;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Documents.Sheets;

namespace Enterprise.Gpt.Api.Startup;

/// <summary>
/// Runs the provisioning and deploy-time checks that must finish before the host serves a request.
/// </summary>
internal static partial class StartupBootstrapper
{
    /// <summary>
    /// Migrates the SQL schema, provisions Cosmos, then validates the summarizer and the File Agent, in that order.
    /// </summary>
    /// <remarks>
    /// Skipped under <see cref="HostEnvironments.Testing"/>: the integration fixture creates its schema with
    /// <c>EnsureCreated</c>, which a prior <c>Migrate()</c> would turn into a no-op, and must not reach Cosmos.
    /// </remarks>
    internal static async Task RunStartupBootstrappersAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        // Built now rather than on the first chat request: it validates the section as it constructs, and a
        // lazily-built singleton would turn a bad edit made after start into a 500 on every turn instead.
        app.Services.GetRequiredService<ISheetQueryOptionsProvider>();

        if (app.Environment.IsEnvironment(HostEnvironments.Testing))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        // Each failure is logged before it is rethrown: the runtime's unhandled-exception printer reaches
        // stderr only, and this is the message an operator reads to find the setting to fix.
        try
        {
            await services.GetRequiredService<SqlSchemaMigrator>().MigrateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            LogMigrationFailed(app.Logger, ex);
            throw;
        }

        await services.GetRequiredService<CosmosResourceProvisioner>().EnsureProvisionedAsync(cancellationToken);

        // After the migration, so a summarizer row a migration corrects is validated as corrected.
        try
        {
            await SummarizerBootstrapper.ValidateAsync(services, app.Logger, cancellationToken);
        }
        catch (Exception ex)
        {
            LogSummarizerUnresolved(app.Logger, ex);
            throw;
        }

        // Regardless of FileAgent:Enabled: a misconfigured agent should fail the deploy, not the first
        // request after somebody switches it on.
        try
        {
            await FileAgentBootstrapper.ValidateAsync(services, FileAgentSkills.DeployedRoot, app.Logger, cancellationToken);
        }
        catch (Exception ex)
        {
            LogFileAgentUncomposed(app.Logger, ex);
            throw;
        }
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "An error occurred while migrating the database")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The configured document summarizer could not be resolved")]
    private static partial void LogSummarizerUnresolved(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The File Agent could not be composed")]
    private static partial void LogFileAgentUncomposed(ILogger logger, Exception exception);

    #endregion
}
