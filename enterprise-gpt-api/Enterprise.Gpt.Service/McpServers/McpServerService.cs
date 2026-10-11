using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.McpServers;
using Enterprise.Gpt.Dto.McpServers;
using Enterprise.Gpt.Entity.McpServers;
using Enterprise.Gpt.Entity.Permissions;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Extensions;
using Enterprise.Gpt.Service.Security;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.McpServers;

public interface IMcpServerService
{
    /// <summary>Gets the active servers the caller holds a permission for, ordered by name.</summary>
    Task<List<McpDto>> GetPermittedMcpServersAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets every server, active and deactivated, ordered by name, for administrators.</summary>
    Task<List<McpServerDto>> GetAllMcpServersAsync(CancellationToken cancellationToken = default);

    /// <exception cref="NotFoundException">No active server with <paramref name="id"/> exists.</exception>
    Task<McpServerDto> GetMcpServerAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Creates a server and its same-named gating permission in one save.</summary>
    /// <exception cref="ValidationException">The name is already in use by an active server or permission.</exception>
    Task<McpServerDto> CreateMcpServerAsync(CreateMcpServerActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an active server, syncing a rename to its permission and invalidating its cached clients.
    /// Changing the URL or auth type of a user-API-key server discards every stored key.
    /// </summary>
    /// <exception cref="ValidationException">The name is already in use by an active server or permission.</exception>
    /// <exception cref="NotFoundException">No active server with <paramref name="id"/> exists.</exception>
    Task<McpServerDto> UpdateMcpServerAsync(Guid id, UpdateMcpServerActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a server with its permission, grants and stored credentials in one save.
    /// </summary>
    /// <exception cref="NotFoundException">No active server with <paramref name="id"/> exists.</exception>
    Task DeactivateMcpServerAsync(Guid id, CancellationToken cancellationToken = default);
}

public partial class McpServerService(ILogger<McpServerService> logger,
    ITokenService tokenService,
    EnterpriseGptDbContext ctx,
    IMcpClientCache mcpClientCache,
    IUserPermissionCache permissionCache,
    TimeProvider timeProvider) : IMcpServerService
{
    private readonly ILogger<McpServerService> _logger = logger;
    private readonly ITokenService _tokenService = tokenService;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IMcpClientCache _mcpClientCache = mcpClientCache;
    private readonly IUserPermissionCache _permissionCache = permissionCache;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public async Task<List<McpDto>> GetPermittedMcpServersAsync(CancellationToken cancellationToken = default)
    {
        var oid = _tokenService.GetOid();

        // Deliberately not IUserPermissionCache: this query runs anyway, so the cache would save only an
        // indexed EXISTS, not a round trip, while making a user-visible list eventually consistent.
        return await _ctx.McpServers
            .AsNoTracking()
            .Where(s => !s.DateDeactivated.HasValue
                && s.Permissions.Any(p => p.UserPermissions.Any(up => up.UserId == oid)))
            .OrderBy(s => s.Name)
            .Select(McpServerMapper.BuildMcpDtoExpression(oid))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<McpServerDto>> GetAllMcpServersAsync(CancellationToken cancellationToken = default)
    {
        return await _ctx.McpServers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(McpServerMapper.MapToMcpServerDtoExpression)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<McpServerDto> GetMcpServerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _ctx.McpServers
            .AsNoTracking()
            .Where(s => s.Id == id && !s.DateDeactivated.HasValue)
            .Select(McpServerMapper.MapToMcpServerDtoExpression)
            .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("MCP server not found.");
    }

    /// <inheritdoc />
    public async Task<McpServerDto> CreateMcpServerAsync(CreateMcpServerActionDto request, CancellationToken cancellationToken = default)
    {
        await EnsureNameIsAvailableAsync(request.Name, excludeServerId: null, cancellationToken);

        var oid = _tokenService.GetOid();

        var newServer = request.FromCreateMcpServerActionDtoToMcpServer();
        newServer.Id = Guid.NewGuid();
        newServer.CreatedById = oid;
        newServer.ModifiedById = oid;

        // Same save, so a server never exists without its gate.
        var permission = new Permission
        {
            Id = Guid.NewGuid(),
            Name = newServer.Name,
            Description = $"Access to the {newServer.Name} MCP server.",
            McpServerId = newServer.Id,
            CreatedById = oid,
            ModifiedById = oid
        };

        _ctx.Add(newServer);
        _ctx.Add(permission);
        await _ctx.SaveChangesAsync(cancellationToken);

        LogMcpServerAndPermissionCreated(_logger, newServer.Id, permission.Id, oid);

        return newServer.MapToMcpServerDto(permission.Id);
    }

    /// <inheritdoc />
    public async Task<McpServerDto> UpdateMcpServerAsync(Guid id, UpdateMcpServerActionDto request, CancellationToken cancellationToken = default)
    {
        var server = await _ctx.McpServers
            .Include(s => s.Permissions)
            .FirstOrDefaultAsync(s => s.Id == id && !s.DateDeactivated.HasValue, cancellationToken)
            ?? throw new NotFoundException("MCP server not found.");

        if (!string.Equals(server.Name, request.Name, StringComparison.Ordinal))
        {
            await EnsureNameIsAvailableAsync(request.Name, excludeServerId: id, cancellationToken);
        }

        var oid = _tokenService.GetOid();
        // A stored key is consent to send it to one endpoint, so repointing the server discards the keys
        // too; otherwise a URL edit silently forwards every user's key to the new host.
        var discardUserApiKeys = server.AuthType == McpAuthTypes.UserApiKey
            && (request.AuthType != McpAuthTypes.UserApiKey
                || !string.Equals(server.Url, request.Url, StringComparison.OrdinalIgnoreCase));

        request.FromUpdateMcpServerActionDtoToMcpServer(server);
        server.ModifiedById = oid;

        // Synced so administrators still recognize the permission in grant listings after a rename.
        var permission = server.Permissions.FirstOrDefault();
        if (permission is not null)
        {
            permission.Name = server.Name;
            permission.Description = $"Access to the {server.Name} MCP server.";
            permission.ModifiedById = oid;
        }

        if (discardUserApiKeys)
        {
            await DeactivateCredentialsAsync(id, oid, cancellationToken);
        }

        await _ctx.SaveChangesOrConflictAsync($"MCP server '{id}' was changed by another request. Reload it and try again.", cancellationToken);

        // Connection details may have changed; cached clients must not outlive them.
        _mcpClientCache.InvalidateServer(id);

        LogMcpServerUpdated(_logger, id, oid);

        return server.MapToMcpServerDto(permission?.Id);
    }

    /// <inheritdoc />
    public async Task DeactivateMcpServerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var server = await _ctx.McpServers
            .Include(s => s.Permissions)
                .ThenInclude(p => p.UserPermissions)
            .Include(s => s.UserCredentials)
            .FirstOrDefaultAsync(s => s.Id == id && !s.DateDeactivated.HasValue, cancellationToken)
            ?? throw new NotFoundException("MCP server not found.");

        var oid = _tokenService.GetOid();
        var date = _timeProvider.GetUtcNow();

        // One atomic save keeps "active grant implies active permission" and leaves no credential behind
        // for a server nobody can select.
        server.DateDeactivated = date;
        server.ModifiedById = oid;

        foreach (var credential in server.UserCredentials)
        {
            credential.DateDeactivated = date;
            credential.ModifiedById = oid;
        }

        foreach (var permission in server.Permissions)
        {
            permission.DateDeactivated = date;
            permission.ModifiedById = oid;

            foreach (var grant in permission.UserPermissions)
            {
                grant.DateDeactivated = date;
                grant.ModifiedById = oid;
            }
        }

        Guid[] affectedUserIds = [.. server.Permissions
            .SelectMany(p => p.UserPermissions)
            .Select(g => g.UserId)
            .Distinct()];

        await _ctx.SaveChangesOrConflictAsync($"MCP server '{id}' was changed by another request. Reload it and try again.", cancellationToken);

        _mcpClientCache.InvalidateServer(id);
        _permissionCache.Invalidate(affectedUserIds);

        LogMcpServerDeactivated(_logger, id, oid);
    }

    #region Private methods

    // Leaves the save to the caller so it lands in the same transaction as the server change.
    private async Task DeactivateCredentialsAsync(Guid mcpServerId, Guid oid, CancellationToken cancellationToken)
    {
        var date = _timeProvider.GetUtcNow();

        var credentials = await _ctx.UserMcpCredentials
            .Where(c => c.McpServerId == mcpServerId)
            .ToListAsync(cancellationToken);

        foreach (var credential in credentials)
        {
            credential.DateDeactivated = date;
            credential.ModifiedById = oid;
        }
    }

    // A clear 400 rather than a unique-index violation at save. Server and permission names share one
    // space because every server creates a same-named permission.
    private async Task EnsureNameIsAvailableAsync(string name, Guid? excludeServerId, CancellationToken cancellationToken)
    {
        var serverNameTaken = await _ctx.McpServers
            .AnyAsync(s => s.Name == name && !s.DateDeactivated.HasValue
                && (excludeServerId == null || s.Id != excludeServerId), cancellationToken);

        var permissionNameTaken = await _ctx.Permissions
            .AnyAsync(p => p.Name == name
                && (excludeServerId == null || p.McpServerId != excludeServerId), cancellationToken);

        if (serverNameTaken || permissionNameTaken)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(McpServer.Name), $"The name '{name}' is already in use by an MCP server or permission.")
            ]);
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server {McpServerId} and permission {PermissionId} created by {UserId}")]
    private static partial void LogMcpServerAndPermissionCreated(
        ILogger logger, Guid mcpServerId, Guid permissionId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server {McpServerId} updated by {UserId}")]
    private static partial void LogMcpServerUpdated(ILogger logger, Guid mcpServerId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server {McpServerId} deactivated by {UserId}")]
    private static partial void LogMcpServerDeactivated(ILogger logger, Guid mcpServerId, Guid userId);

    #endregion
}
