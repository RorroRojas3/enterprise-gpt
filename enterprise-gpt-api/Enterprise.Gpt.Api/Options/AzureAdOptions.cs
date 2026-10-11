namespace Enterprise.Gpt.Api.Options;

/// <summary>The Entra ID application this API runs as.</summary>
/// <remarks>
/// No startup validator: Microsoft.Identity.Web checks this section itself, and a blank Graph credential
/// fails when the Graph client is first built.
/// </remarks>
internal sealed class AzureAdOptions
{
    internal const string SectionName = "AzureAd";

    public string? TenantId { get; set; }

    /// <summary>The application (client) id, which is also the token audience.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client secret the Microsoft Graph client authenticates with.</summary>
    public string? ClientSecret { get; set; }
}
