namespace Enterprise.Gpt.Common.Constants;

/// <summary>
/// The final segment of each application-specific problem type URI, <c>/problems/{code}</c>.
/// </summary>
/// <remarks>
/// Clients match the resulting URIs verbatim, so changing a value is a breaking API change.
/// </remarks>
public static class ProblemCodes
{
    public const string ValidationError = "validation-error";

    public const string UploadTooLarge = "upload-too-large";

    public const string ResourceNotFound = "resource-not-found";

    public const string Forbidden = "forbidden";

    public const string PermissionRequired = "permission-required";

    public const string ConversationBusy = "conversation-busy";

    public const string McpServerUnavailable = "mcp-server-unavailable";

    public const string McpCredentialRequired = "mcp-credential-required";

    public const string McpCredentialRejected = "mcp-credential-rejected";

    public const string ProviderNotConfigured = "provider-not-configured";

    public const string StorageNotConfigured = "storage-not-configured";

    public const string ExportRendererNotConfigured = "export-renderer-not-configured";
}
