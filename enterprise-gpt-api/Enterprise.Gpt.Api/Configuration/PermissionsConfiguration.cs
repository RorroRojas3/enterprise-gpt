using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Permissions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers permissions and the per-user permission cache authorization reads.
/// </summary>
internal static class PermissionsConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers the permission service and the cache keyed by Entra object id.
    /// </summary>
    /// <remarks>
    /// The cache is written at sign-in and on a miss and invalidated per user by the services that change grants.
    /// Its absolute lifetime exists because that invalidation is per instance.
    /// </remarks>
    internal static IServiceCollection AddPermissions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<UserPermissionCacheOptions>, UserPermissionCacheOptionsValidator>();
        services.AddOptions<UserPermissionCacheOptions>()
            .Bind(configuration.GetSection(UserPermissionCacheOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IUserPermissionCache, UserPermissionCache>();
        services.AddScoped<IUserGrantReader, UserGrantReader>();
        services.AddScoped<IPermissionService, PermissionService>();

        return services;
    }

    #endregion
}
