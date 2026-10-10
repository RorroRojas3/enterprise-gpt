using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Authentication handler that replaces the Entra ID JWT bearer scheme under test. It issues a
/// principal carrying the AAD object-identifier claim <c>TokenService.GetOid()</c> reads, based
/// on the <see cref="UserHeader"/> request header: <c>admin</c> and <c>user</c> map to the
/// fixed <see cref="TestUsers"/> identities, any raw GUID is used verbatim, and a missing
/// header produces an unauthenticated request (401 on protected endpoints).
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>
    /// The authentication scheme name registered by the test factory.
    /// </summary>
    public const string SchemeName = "Test";

    /// <summary>
    /// The request header selecting which test identity the request runs as.
    /// </summary>
    public const string UserHeader = "X-Test-User";

    private const string _oidClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var oid = values.ToString() switch
        {
            "admin" => TestUsers.AdminUserId,
            "user" => TestUsers.RegularUserId,
            var raw => Guid.Parse(raw)
        };

        var identity = new ClaimsIdentity([new Claim(_oidClaimType, oid.ToString())], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
