using Enterprise.Gpt.Service.Users;

namespace Enterprise.Gpt.Api.Configuration;

internal static class UsersConfiguration
{
    #region Public static methods

    internal static IServiceCollection AddUsers(this IServiceCollection services)
    {
        services.AddSingleton<IGraphService, GraphService>();
        services.AddScoped<IUserService, UserService>();

        return services;
    }

    #endregion
}
