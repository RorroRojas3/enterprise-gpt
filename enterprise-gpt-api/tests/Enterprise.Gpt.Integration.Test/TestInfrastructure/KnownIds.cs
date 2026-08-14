using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Well-known identifiers inserted by the HasData seeds when the database is created.
/// </summary>
public static class KnownIds
{
    /// <summary>
    /// The system user seeded by <c>UserConfiguration</c>.
    /// </summary>
    public static readonly Guid SeedUserId = Guid.Parse("5f7ab694-1b6c-4b19-badd-c82b65e794cf");

    /// <summary>
    /// The Azure OpenAI provider seeded by <c>ProviderConfiguration</c>.
    /// </summary>
    public static readonly Guid SeedProviderId = Providers.AzureOpenAI;

    /// <summary>
    /// The Azure AI Foundry provider seeded by <c>ProviderConfiguration</c>.
    /// </summary>
    public static readonly Guid SeedFoundryProviderId = Providers.AzureAIFoundry;

    /// <summary>
    /// The "rr-gpt-5.6-luna" model seeded by <c>ModelConfiguration</c> (active, non-default).
    /// </summary>
    public static readonly Guid SeedModelId = Guid.Parse("c36e22ed-262a-47a1-b2ba-06a38355ae0f");

    /// <summary>
    /// The built-in Administrator permission seeded by <c>PermissionConfiguration</c>.
    /// </summary>
    public static readonly Guid AdministratorPermissionId = PermissionIds.Administrator;
}
