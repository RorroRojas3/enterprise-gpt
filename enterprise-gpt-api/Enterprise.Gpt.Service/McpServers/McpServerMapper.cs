using System.Linq.Expressions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.McpServers;
using Enterprise.Gpt.Dto.McpServers;
using Enterprise.Gpt.Entity.McpServers;

namespace Enterprise.Gpt.Service.McpServers;

public static class McpServerMapper
{
    /// <summary>Projection to <see cref="McpServerDto"/>, resolving the permission id in-query.</summary>
    public static Expression<Func<McpServer, McpServerDto>> MapToMcpServerDtoExpression { get; } =
        server => new McpServerDto
        {
            Id = server.Id,
            Name = server.Name,
            Description = server.Description,
            Url = server.Url,
            AuthType = server.AuthType,
            Scope = server.Scope,
            IconKey = server.IconKey,
            Headers = server.Headers,
            PermissionId = server.Permissions
                .Where(p => !p.DateDeactivated.HasValue)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefault(),
            DateDeactivated = server.DateDeactivated
        };

    #region Private methods

    // Empty collapses to null so `Headers IS NULL` is the only "none". Case-insensitive like HTTP, so two
    // spellings of one name throw here; the validators refuse that set first.
    private static Dictionary<string, string>? ToStoredHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        return headers is null or { Count: 0 }
            ? null
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Public static methods

    public static McpServerDto MapToMcpServerDto(this McpServer server, Guid? permissionId = null)
    {
        return new McpServerDto
        {
            Id = server.Id,
            Name = server.Name,
            Description = server.Description,
            Url = server.Url,
            AuthType = server.AuthType,
            Scope = server.Scope,
            IconKey = server.IconKey,
            Headers = server.Headers,
            PermissionId = permissionId,
            DateDeactivated = server.DateDeactivated
        };
    }

    /// <summary>
    /// Projection to <see cref="McpDto"/>, closed over the caller because the credential fields differ per
    /// user; resolving them here keeps the listing to one query.
    /// </summary>
    public static Expression<Func<McpServer, McpDto>> BuildMcpDtoExpression(Guid userOid)
    {
        return server => new McpDto
        {
            Id = server.Id,
            Name = server.Name,
            Description = server.Description,
            IconKey = server.IconKey,
            RequiresUserApiKey = server.AuthType == McpAuthTypes.UserApiKey,
            // A rejected credential reads as absent, so the picker asks for a new one instead
            // of offering a server the next turn would fail on.
            HasUserApiKey = server.AuthType == McpAuthTypes.UserApiKey
                && server.UserCredentials.Any(c => c.UserId == userOid
                    && !c.DateDeactivated.HasValue
                    && !c.DateRejected.HasValue),
            ApiKeyHint = server.UserCredentials
                .Where(c => c.UserId == userOid && !c.DateDeactivated.HasValue && !c.DateRejected.HasValue)
                .Select(c => c.ApiKeyHint)
                .FirstOrDefault()
        };
    }

    /// <summary>Maps a credential to its status; a rejected one reads as absent.</summary>
    public static McpCredentialStatusDto MapToMcpCredentialStatusDto(
        this UserMcpCredential? credential, Guid mcpServerId)
    {
        return credential is null || credential.DateRejected.HasValue
            ? new McpCredentialStatusDto { McpServerId = mcpServerId }
            : new McpCredentialStatusDto
            {
                McpServerId = mcpServerId,
                HasApiKey = true,
                ApiKeyHint = credential.ApiKeyHint,
                DateModified = credential.DateModified
            };
    }

    /// <summary>
    /// Creates an untracked server; callers set <c>Id</c>, <c>CreatedById</c> and <c>ModifiedById</c>.
    /// </summary>
    public static McpServer FromCreateMcpServerActionDtoToMcpServer(this CreateMcpServerActionDto dto)
    {
        return new McpServer
        {
            Name = dto.Name,
            Description = dto.Description,
            Url = dto.Url,
            AuthType = dto.AuthType,
            Scope = dto.Scope,
            IconKey = dto.IconKey,
            Headers = ToStoredHeaders(dto.Headers)
        };
    }

    /// <summary>
    /// Applies the update to a tracked server in place; callers set <c>ModifiedById</c> and save.
    /// </summary>
    public static void FromUpdateMcpServerActionDtoToMcpServer(this UpdateMcpServerActionDto dto, McpServer server)
    {
        server.Name = dto.Name;
        server.Description = dto.Description;
        server.Url = dto.Url;
        server.AuthType = dto.AuthType;
        server.Scope = dto.Scope;
        server.IconKey = dto.IconKey;
        server.Headers = ToStoredHeaders(dto.Headers);
    }

    #endregion
}
