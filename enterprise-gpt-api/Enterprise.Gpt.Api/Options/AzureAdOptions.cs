namespace Enterprise.Gpt.Api.Options;

/// <summary>
/// The Entra ID application this API runs as, bound from the <c>AzureAd</c> configuration section.
/// </summary>
/// <remarks>
/// No startup validator: Microsoft.Identity.Web binds and checks this section itself, and a blank Graph
/// credential fails when the Graph client is first built.
/// </remarks>
internal sealed class AzureAdOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    internal const string SectionName = "AzureAd";

    /// <summary>The directory (tenant) id.</summary>
    public string? TenantId { get; set; }

    /// <summary>The application (client) id, which is also the token audience.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client secret the Microsoft Graph client authenticates with.</summary>
    public string? ClientSecret { get; set; }
}
