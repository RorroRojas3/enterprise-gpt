using Enterprise.Gpt.Service.Security;

namespace Enterprise.Gpt.Api.Configuration;

internal static class SecurityConfiguration
{
    #region Public static methods

    internal static IServiceCollection AddEnterpriseGptSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IUserSecretProtector, UserSecretProtector>();

        return services;
    }

    #endregion
}
