using Enterprise.Gpt.Service.Security;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers caller identity and secret protection.
/// </summary>
internal static class SecurityConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers the token service that reads the caller from the request, and the secret protector.
    /// </summary>
    internal static IServiceCollection AddEnterpriseGptSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IUserSecretProtector, UserSecretProtector>();

        return services;
    }

    #endregion
}
