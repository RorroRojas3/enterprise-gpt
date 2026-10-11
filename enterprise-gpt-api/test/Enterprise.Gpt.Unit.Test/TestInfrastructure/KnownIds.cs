using Enterprise.Gpt.Common.Constants;

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

    public static readonly Guid SeedProviderId = Providers.AzureOpenAI;

    /// <summary>
    /// The seeded "rr-gpt5.6-luna" model: the pinned document summarizer, so active, non-default and
    /// hidden from the chat model picker.
    /// </summary>
    public static readonly Guid SeedModelId = Guid.Parse("c36e22ed-262a-47a1-b2ba-06a38355ae0f");

    public static readonly Guid AdministratorPermissionId = PermissionIds.Administrator;

    /// <summary>
    /// Seeded with <c>IsDefault</c>, so every provisioning path grants it and exact permission-set
    /// assertions must include it.
    /// </summary>
    public static readonly Guid UploadFilePermissionId = PermissionIds.UploadFile;

    /// <summary>Not <c>IsDefault</c>, so no provisioning path grants it.</summary>
    public static readonly Guid GenerateFilesPermissionId = PermissionIds.GenerateFiles;
}
