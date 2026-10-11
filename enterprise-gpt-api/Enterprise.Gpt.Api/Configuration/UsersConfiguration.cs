using Enterprise.Gpt.Service.Users;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers users and their Microsoft Graph lookups.
/// </summary>
internal static class UsersConfiguration
{
    #region Public static methods

    /// <summary>Registers the user service and the Graph service.</summary>
    internal static IServiceCollection AddUsers(this IServiceCollection services)
    {
        services.AddSingleton<IGraphService, GraphService>();
        services.AddScoped<IUserService, UserService>();

        return services;
    }

    #endregion
}
