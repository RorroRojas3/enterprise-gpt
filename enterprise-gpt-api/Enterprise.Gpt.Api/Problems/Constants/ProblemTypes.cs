using System.Collections.Frozen;
using Enterprise.Gpt.Api.Problems.Models;
using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Api.Problems.Constants;

/// <summary>
/// The application-specific RFC 9457 problem types this API emits. A problem with no entry here carries
/// no <c>type</c>, so ASP.NET Core falls back to the RFC 9110 status-section link.
/// </summary>
internal static class ProblemTypes
{
    /// <remarks>
    /// A relative URI (RFC 9457 §3.1.1). Clients match these verbatim as opaque identifiers, so changing
    /// one is a breaking API change.
    /// </remarks>
    public const string BaseUri = "/problems/";

    /// <summary>One or more request fields failed validation. Accompanied by an <c>errors</c> dictionary.</summary>
    public static readonly ProblemType Validation =
        new($"{BaseUri}{ProblemCodes.ValidationError}", "One or more validation errors occurred.");

    /// <summary>The request body exceeded the configured upload size limit. Accompanied by a <c>maxBytes</c> extension.</summary>
    public static readonly ProblemType UploadTooLarge =
        new($"{BaseUri}{ProblemCodes.UploadTooLarge}", "Upload too large");

    /// <summary>The requested resource does not exist, is deactivated, or does not belong to the caller.</summary>
    public static readonly ProblemType NotFound =
        new($"{BaseUri}{ProblemCodes.ResourceNotFound}", "Resource not found");

    /// <summary>The caller is authenticated but is not allowed to perform the request.</summary>
    public static readonly ProblemType Forbidden =
        new($"{BaseUri}{ProblemCodes.Forbidden}", "Access denied");

    /// <summary>The caller lacks one or more named permission grants. Accompanied by a <c>permissions</c> extension.</summary>
    public static readonly ProblemType PermissionRequired =
        new($"{BaseUri}{ProblemCodes.PermissionRequired}", "Permission required");

    /// <summary>The conversation already has a turn in flight; the caller can retry.</summary>
    public static readonly ProblemType ConversationBusy =
        new($"{BaseUri}{ProblemCodes.ConversationBusy}", "Conversation is busy");

    /// <summary>
    /// An MCP server is unreachable or its on-behalf-of token could not be acquired; consent prompts count
    /// too, as servers are consented tenant-wide. Accompanied by a <c>serverName</c> extension.
    /// </summary>
    public static readonly ProblemType McpServerUnavailable =
        new($"{BaseUri}{ProblemCodes.McpServerUnavailable}", "MCP server unavailable");

    /// <summary>
    /// An MCP server needs an API key the caller supplies themselves and none is stored.
    /// Accompanied by <c>mcpServerId</c> and <c>serverName</c> extensions.
    /// </summary>
    public static readonly ProblemType McpCredentialRequired =
        new($"{BaseUri}{ProblemCodes.McpCredentialRequired}", "API key required");

    /// <summary>
    /// An MCP server refused the API key the caller stored. Accompanied by <c>mcpServerId</c> and
    /// <c>serverName</c> extensions.
    /// </summary>
    /// <remarks>Split from <see cref="McpCredentialRequired"/> only so the client can word it apart.</remarks>
    public static readonly ProblemType McpCredentialRejected =
        new($"{BaseUri}{ProblemCodes.McpCredentialRejected}", "API key rejected");

    /// <summary>
    /// The selected model's provider has no chat client in this deployment. Accompanied by a
    /// <c>providerId</c> extension.
    /// </summary>
    public static readonly ProblemType ProviderNotConfigured =
        new($"{BaseUri}{ProblemCodes.ProviderNotConfigured}", "Provider not configured");

    /// <summary>
    /// Blob storage cannot sign a download link in this deployment, so the file behind a document
    /// cannot be handed out.
    /// </summary>
    public static readonly ProblemType StorageNotConfigured =
        new($"{BaseUri}{ProblemCodes.StorageNotConfigured}", "Storage not configured");

    /// <summary>
    /// No renderer for the requested export format (withdrawn by configuration, or no usable PDF font).
    /// Accompanied by a <c>format</c> extension so a client can disable just that format.
    /// </summary>
    public static readonly ProblemType ExportRendererNotConfigured =
        new($"{BaseUri}{ProblemCodes.ExportRendererNotConfigured}", "Export format not available");

    // Declared after every field it reads: static initializers run in textual order.
    private static readonly FrozenDictionary<string, ProblemType> _byCode = new[]
    {
        Validation, UploadTooLarge, NotFound, Forbidden, PermissionRequired, ConversationBusy, McpServerUnavailable,
        McpCredentialRequired, McpCredentialRejected, ProviderNotConfigured, StorageNotConfigured, ExportRendererNotConfigured
    }.ToFrozenDictionary(problemType => problemType.Type[BaseUri.Length..], StringComparer.Ordinal);

    #region Public static methods

    /// <summary>
    /// Gets the problem type a <see cref="ProblemCodes"/> value names, or <see langword="null"/> when it names none.
    /// </summary>
    public static ProblemType? ForCode(string problemCode) =>
        _byCode.TryGetValue(problemCode, out var problemType) ? problemType : null;

    #endregion
}
