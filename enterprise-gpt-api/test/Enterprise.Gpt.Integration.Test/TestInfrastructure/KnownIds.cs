using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Well-known identifiers inserted by the HasData seeds when the database is created.
/// </summary>
public static class KnownIds
{
    public static readonly Guid SeedUserId = Guid.Parse("5f7ab694-1b6c-4b19-badd-c82b65e794cf");

    public static readonly Guid SeedProviderId = Providers.AzureOpenAI;

    /// <summary>
    /// The pinned document summarizer: active, non-default and hidden from the chat model picker.
    /// </summary>
    public static readonly Guid SeedModelId = Guid.Parse("c36e22ed-262a-47a1-b2ba-06a38355ae0f");

    public static readonly Guid AdministratorPermissionId = PermissionIds.Administrator;

    /// <summary>Not <c>IsDefault</c>, so no test user holds it until a test grants it.</summary>
    public static readonly Guid GenerateFilesPermissionId = PermissionIds.GenerateFiles;
}
