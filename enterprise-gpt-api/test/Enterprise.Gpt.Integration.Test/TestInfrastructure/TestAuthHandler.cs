using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Replaces Entra ID under test: a <see cref="UserHeader"/> of <c>admin</c> or <c>user</c> maps to
/// <see cref="TestUsers"/>, any GUID is used verbatim, and no header leaves the request anonymous.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

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
