using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Well-known identifiers inserted by the HasData seeds when the database is created.
/// </summary>
public static class KnownIds
{
    /// <summary>
    /// The system user seeded by <c>UserConfiguration</c>; used as the OID returned by the
    /// substituted <c>ITokenService</c> so audit foreign keys resolve.
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
    /// The Amazon Bedrock provider seeded by <c>ProviderConfiguration</c>.
    /// </summary>
    public static readonly Guid SeedBedrockProviderId = Providers.AmazonBedrock;

    /// <summary>
    /// The Anthropic provider seeded by <c>ProviderConfiguration</c>.
    /// </summary>
    public static readonly Guid SeedAnthropicProviderId = Providers.Anthropic;

    /// <summary>
    /// The "rr-gpt-5.6-luna" model seeded by <c>ModelConfiguration</c> (active, non-default).
    /// </summary>
    public static readonly Guid SeedModelId = Guid.Parse("c36e22ed-262a-47a1-b2ba-06a38355ae0f");

    /// <summary>
    /// The built-in Administrator permission seeded by <c>PermissionConfiguration</c>.
    /// </summary>
    public static readonly Guid AdministratorPermissionId = PermissionIds.Administrator;

    /// <summary>
    /// The built-in Upload File permission seeded by <c>PermissionConfiguration</c>. It is seeded with
    /// <c>IsDefault</c> set, so every provisioning path grants it and tests asserting on exact permission
    /// sets must account for it.
    /// </summary>
    public static readonly Guid UploadFilePermissionId = PermissionIds.UploadFile;
}
