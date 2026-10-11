using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Security;

public interface ITokenService
{
    /// <summary>Returns the current user's Entra object id (OID) from the first identity's claims.</summary>
    /// <exception cref="UnauthorizedAccessException">There is no authenticated user or no OID claim.</exception>
    /// <exception cref="FormatException">The OID claim is not a valid GUID.</exception>
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
