using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Permissions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Enterprise.Gpt.Api.Configuration;

internal static class PermissionsConfiguration
{
    #region Public static methods

    /// <remarks>
    /// The permission cache is invalidated per user by the services that change grants; its absolute lifetime
    /// exists because that invalidation is per instance.
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
