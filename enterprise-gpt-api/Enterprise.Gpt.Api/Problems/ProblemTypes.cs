namespace Enterprise.Gpt.Api.Problems;

/// <summary>
/// The application-specific RFC 9457 problem types this API emits. A problem with no entry here
/// carries no <c>type</c>, which is what lets ASP.NET Core fall back to the RFC 9110
/// status-section link.
/// </summary>
internal static class ProblemTypes
{
    /// <summary>
    /// Prefix shared by every application-specific problem type URI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relative URI reference, which RFC 9457 §3.1.1 permits and which does not require the API to
    /// own a documentation host. The trade-off is that resolution is base-URL dependent, so clients
    /// must treat these as opaque identifiers rather than resolving them.
    /// </para>
    /// <para>
    /// These URIs are identifiers, not links: clients match on them verbatim, so changing one is a
    /// breaking API change.
    /// </para>
    /// </remarks>
    public const string BaseUri = "/problems/";

    /// <summary>One or more request fields failed validation. Accompanied by an <c>errors</c> dictionary.</summary>
    public static readonly ProblemType Validation =
        new($"{BaseUri}validation-error", "One or more validation errors occurred.");

    /// <summary>The request body exceeded the configured upload size limit. Accompanied by a <c>maxBytes</c> extension.</summary>
    public static readonly ProblemType UploadTooLarge =
        new($"{BaseUri}upload-too-large", "Upload too large");

    /// <summary>The requested resource does not exist, is deactivated, or does not belong to the caller.</summary>
    public static readonly ProblemType NotFound =
        new($"{BaseUri}resource-not-found", "Resource not found");

    /// <summary>The caller is authenticated but is not allowed to perform the request.</summary>
    public static readonly ProblemType Forbidden =
        new($"{BaseUri}forbidden", "Access denied");

    /// <summary>The caller lacks one or more named permission grants. Accompanied by a <c>permissions</c> extension.</summary>
    public static readonly ProblemType PermissionRequired =
        new($"{BaseUri}permission-required", "Permission required");

    /// <summary>The conversation already has a turn in flight; the caller can retry.</summary>
    public static readonly ProblemType ConversationBusy =
        new($"{BaseUri}conversation-busy", "Conversation is busy");

    /// <summary>
    /// An MCP server needs interactive consent or a Conditional Access challenge. Accompanied by a
    /// <c>serverName</c> extension. Carried on a 403 rather than a 401, and the distinct type URI is
    /// what lets a client prompt for consent instead of retrying the token.
    /// </summary>
    public static readonly ProblemType McpAuthorizationRequired =
        new($"{BaseUri}mcp-authorization-required", "MCP authorization required");

    /// <summary>An MCP server could not be reached. Accompanied by a <c>serverName</c> extension.</summary>
    public static readonly ProblemType McpServerUnavailable =
        new($"{BaseUri}mcp-server-unavailable", "MCP server unavailable");
}
