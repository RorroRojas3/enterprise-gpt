using System.Collections.Frozen;
using Enterprise.Gpt.Api.Problems.Models;
using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Api.Problems.Constants;

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
    /// An MCP server could not be used: unreachable, or its on-behalf-of token could not be
    /// acquired — a consent or Conditional Access requirement included, since these servers are
    /// consented tenant-wide and a UI-required result is a registration fault rather than
    /// something the caller can act on. Accompanied by a <c>serverName</c> extension.
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
    /// <remarks>
    /// Separate from <see cref="McpCredentialRequired"/> only so the client can word the two
    /// differently; both mean the user has to supply a key before the request can proceed.
    /// </remarks>
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
    /// A conversation export was asked for in a format this deployment has no renderer for — the
    /// format is withdrawn by configuration, or PDF rendering found no usable font. Accompanied by a
    /// <c>format</c> extension carrying the wire token, so a client offering several formats can
    /// disable the one rather than the control.
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
