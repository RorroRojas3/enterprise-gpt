using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Repository.Sql.Options;
using Enterprise.Gpt.Repository.Sql.Provisioning;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Repository.Sql;

/// <summary>
/// Registers the SQL Server store.
/// </summary>
public static class SqlPersistenceConfiguration
{
    /// <summary>
    /// Registers the validated connection options, the <see cref="EnterpriseGptDbContext"/> and the schema migrator.
    /// </summary>
    public static IServiceCollection AddEnterpriseGptSqlPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidator<SqlDbOptions>, SqlDbOptionsValidator>();
        services.AddOptions<SqlDbOptions>()
            .Bind(configuration.GetSection(SqlDbOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        // The connection string is read when a context is built, so a test host's override still wins.
        services.AddDbContext<EnterpriseGptDbContext>((services, options) => options.UseSqlServer(
            services.GetRequiredService<IOptions<SqlDbOptions>>().Value.DefaultConnection,
            // SQL Server 2025: pre-2025 engines and LocalDB reject this compatibility level.
            sql => sql.UseCompatibilityLevel(170)));
        services.AddScoped<SqlSchemaMigrator>();

        return services;
    }

    /// <summary>
    /// Adds the probe that proves the application's own database connection works.
    /// </summary>
    public static IHealthChecksBuilder AddEnterpriseGptSqlHealthCheck(this IHealthChecksBuilder builder, string name, IEnumerable<string> tags) =>
        builder.AddDbContextCheck<EnterpriseGptDbContext>(name, tags: tags);
}
