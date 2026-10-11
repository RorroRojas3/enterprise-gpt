using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Security;

public interface ITokenService
{
    /// <summary>
    /// Retrieves the Azure AD Object ID (OID) for the current authenticated user from the HTTP context.
    /// </summary>
    /// <returns>
    /// The OID as a <see cref="Guid"/> if the claim exists and is valid; otherwise, <c>null</c>.
    /// </returns>
    /// <remarks>
    /// Reads the claim of type 'http://schemas.microsoft.com/identity/claims/objectidentifier' from the first identity
    /// on the current <see cref="System.Security.Claims.ClaimsPrincipal"/>. If no user is present in the HTTP context,
    /// a warning is logged and <c>null</c> is returned.
    /// </remarks>
    /// <exception cref="FormatException">
    /// Thrown if the OID claim is present but its value is not a valid GUID string.
    /// </exception>
    Guid GetOid();
}

public partial class TokenService(ILogger<TokenService> logger,
    IHttpContextAccessor httpContextAccessor) : ITokenService
{
    private readonly ILogger<TokenService> _logger = logger;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private const string _oidClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <inheritdoc />
    public Guid GetOid()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user is null)
        {
            LogNoAuthenticatedUserFound(_logger);
            throw new UnauthorizedAccessException("No authenticated user found in the HTTP context.");
        }

        var oidClaim = user?.Identities?.FirstOrDefault()?.Claims?.FirstOrDefault(x => x.Type == _oidClaimType);
        return oidClaim is not null ? Guid.Parse(oidClaim.Value) : throw new UnauthorizedAccessException("No OID claim found.");
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "No authenticated user found in the HTTP context.")]
    private static partial void LogNoAuthenticatedUserFound(ILogger logger);

    #endregion
}
