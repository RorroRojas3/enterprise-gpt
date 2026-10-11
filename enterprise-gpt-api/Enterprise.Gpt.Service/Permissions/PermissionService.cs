using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Dto.Actions.Permissions;
using Enterprise.Gpt.Dto.Permissions;
using Enterprise.Gpt.Entity.Permissions;
using Enterprise.Gpt.Repository.Sql.Constants;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Extensions;
using Enterprise.Gpt.Service.Security;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Permissions;

public interface IPermissionService
{
    /// <summary>
    /// Gets all active permissions, ordered by name.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The list of active permissions.</returns>
    Task<List<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets every permission, active and deactivated, ordered by name.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The list of all permissions.</returns>
    Task<List<PermissionDto>> GetAllPermissionsIncludingDeactivatedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single active permission by id.
    /// </summary>
    /// <param name="id">The permission id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The matching permission.</returns>
    /// <exception cref="NotFoundException">No active permission with <paramref name="id"/> exists.</exception>
    Task<PermissionDto> GetPermissionAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new custom permission after validating the request.
    /// </summary>
    /// <param name="request">The creation request.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The created permission.</returns>
    /// <exception cref="ValidationException">The name is already in use by an active permission or MCP server.</exception>
    Task<PermissionDto> CreatePermissionAsync(CreatePermissionActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing active custom permission after validating the request.
    /// </summary>
    /// <param name="id">The id of the permission to update.</param>
    /// <param name="request">The update request.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The updated permission.</returns>
    /// <exception cref="ValidationException">The permission is a built-in, the permission is managed by an MCP server, or the name is already in use.</exception>
    /// <exception cref="NotFoundException">No active permission with <paramref name="id"/> exists.</exception>
    Task<PermissionDto> UpdatePermissionAsync(Guid id, UpdatePermissionActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a custom permission together with every active grant of it, in one
    /// atomic save.
    /// </summary>
    /// <param name="id">The id of the permission to deactivate.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="ValidationException">The permission is a built-in or is managed by an MCP server.</exception>
    /// <exception cref="NotFoundException">No active permission with <paramref name="id"/> exists.</exception>
    Task DeactivatePermissionAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants a permission to a user. Granting an already-held permission is an idempotent
    /// no-op; a previously revoked grant is re-established with a fresh row.
    /// </summary>
    /// <param name="userId">The id of the user receiving the grant.</param>
    /// <param name="permissionId">The id of the permission to grant.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="NotFoundException">The user or the permission does not exist or is deactivated.</exception>
    Task GrantPermissionAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a user's active grant of a permission by soft-deleting the grant row.
    /// </summary>
    /// <param name="userId">The id of the user losing the grant.</param>
    /// <param name="permissionId">The id of the permission to revoke.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="ValidationException">The caller is revoking their own Administrator permission.</exception>
    /// <exception cref="NotFoundException">The user holds no active grant of the permission.</exception>
    Task RevokePermissionAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken = default);
}

public partial class PermissionService(ILogger<PermissionService> logger,
    ITokenService tokenService,
    EnterpriseGptDbContext ctx,
    IUserPermissionCache permissionCache,
    TimeProvider timeProvider) : IPermissionService
{
    private readonly ILogger<PermissionService> _logger = logger;
    private readonly ITokenService _tokenService = tokenService;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IUserPermissionCache _permissionCache = permissionCache;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public async Task<List<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken = default)
    {
        return await _ctx.Permissions
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(PermissionMapper.MapToPermissionDtoExpression)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<PermissionDto>> GetAllPermissionsIncludingDeactivatedAsync(CancellationToken cancellationToken = default)
    {
        return await _ctx.Permissions
            .IgnoreQueryFilters([QueryFilterNames.SoftDelete])
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(PermissionMapper.MapToPermissionDtoExpression)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PermissionDto> GetPermissionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _ctx.Permissions
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(PermissionMapper.MapToPermissionDtoExpression)
            .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Permission not found.");
    }

    /// <inheritdoc />
    public async Task<PermissionDto> CreatePermissionAsync(CreatePermissionActionDto request, CancellationToken cancellationToken = default)
    {
        await EnsureNameIsAvailableAsync(request.Name, excludePermissionId: null, cancellationToken);

        var oid = _tokenService.GetOid();

        var newPermission = request.FromCreatePermissionActionDtoToPermission();
        newPermission.Id = Guid.NewGuid();
        newPermission.CreatedById = oid;
        newPermission.ModifiedById = oid;

        _ctx.Add(newPermission);
        await _ctx.SaveChangesAsync(cancellationToken);

        LogPermissionCreated(_logger, newPermission.Id, oid);

        return newPermission.MapToPermissionDto();
    }

    /// <inheritdoc />
    public async Task<PermissionDto> UpdatePermissionAsync(Guid id, UpdatePermissionActionDto request, CancellationToken cancellationToken = default)
    {
        var permission = await _ctx.Permissions
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("Permission not found.");

        EnsurePermissionIsCustom(permission);

        if (!string.Equals(permission.Name, request.Name, StringComparison.Ordinal))
        {
            await EnsureNameIsAvailableAsync(request.Name, excludePermissionId: id, cancellationToken);
        }

        var oid = _tokenService.GetOid();

        request.FromUpdatePermissionActionDtoToPermission(permission);
        permission.ModifiedById = oid;

        await _ctx.SaveChangesOrConflictAsync($"Permission '{id}' was changed by another request. Reload it and try again.", cancellationToken);

        LogPermissionUpdated(_logger, id, oid);

        return permission.MapToPermissionDto();
    }

    /// <inheritdoc />
    public async Task DeactivatePermissionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var permission = await _ctx.Permissions
            .Include(p => p.UserPermissions)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("Permission not found.");

        EnsurePermissionIsCustom(permission);

        var oid = _tokenService.GetOid();
        var date = _timeProvider.GetUtcNow();

        // Tracked entities so the permission and its grants deactivate in one atomic save,
        // preserving the invariant that an active grant implies an active permission.
        permission.DateDeactivated = date;
        permission.ModifiedById = oid;

        foreach (var grant in permission.UserPermissions)
        {
            grant.DateDeactivated = date;
            grant.ModifiedById = oid;
        }

        // Collected from the already-included graph, so the cascade costs no extra query.
        Guid[] affectedUserIds = [.. permission.UserPermissions.Select(g => g.UserId).Distinct()];

        await _ctx.SaveChangesOrConflictAsync($"Permission '{id}' was changed by another request. Reload it and try again.", cancellationToken);

        _permissionCache.Invalidate(affectedUserIds);

        LogPermissionDeactivated(_logger, id, oid);
    }

    /// <inheritdoc />
    public async Task GrantPermissionAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken = default)
    {
        var userExists = await _ctx.Users
            .AnyAsync(u => u.Id == userId && !u.DateDeactivated.HasValue, cancellationToken);
        if (!userExists)
        {
            throw new NotFoundException("User not found.");
        }

        var permissionExists = await _ctx.Permissions
            .AnyAsync(p => p.Id == permissionId, cancellationToken);
        if (!permissionExists)
        {
            throw new NotFoundException("Permission not found.");
        }

        var alreadyGranted = await _ctx.UserPermissions
            .AnyAsync(up => up.UserId == userId && up.PermissionId == permissionId, cancellationToken);
        if (alreadyGranted)
        {
            return;
        }

        var oid = _tokenService.GetOid();

        // A previously revoked pair inserts a fresh row; the filtered unique index only
        // covers active rows, so revoked history is preserved.
        _ctx.Add(new UserPermission
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionId = permissionId,
            CreatedById = oid,
            ModifiedById = oid
        });
        await _ctx.SaveChangesAsync(cancellationToken);

        _permissionCache.Invalidate(userId);

        LogPermissionGrantedToUser(_logger, permissionId, userId, oid);
    }

    /// <inheritdoc />
    public async Task RevokePermissionAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken = default)
    {
        var oid = _tokenService.GetOid();

        // The same invariant UserService.UpdateUserAsync enforces on the bulk permission
        // diff. Both routes can revoke this grant, so guarding only one would leave the
        // lockout reachable by calling this endpoint with your own id.
        if (userId == oid && permissionId == PermissionIds.Administrator)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(UserPermission.PermissionId),
                    "Administrators cannot revoke their own Administrator permission.")
            ]);
        }

        var date = _timeProvider.GetUtcNow();

        var rows = await _ctx.UserPermissions
            .Where(up => up.UserId == userId && up.PermissionId == permissionId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.DateDeactivated, date)
                .SetProperty(p => p.DateModified, date)
                .SetProperty(p => p.ModifiedById, oid), cancellationToken);

        if (rows == 0)
        {
            throw new NotFoundException("The user holds no active grant of this permission.");
        }

        // ExecuteUpdateAsync bypasses the change tracker, so rows > 0 is the only signal that
        // anything actually changed.
        _permissionCache.Invalidate(userId);

        LogPermissionRevokedFromUser(_logger, permissionId, userId, oid);
    }

    #region Private methods

    /// <summary>
    /// Throws when the permission is one of the built-ins or is managed by an MCP server;
    /// those rows follow their own lifecycles and must not be edited directly.
    /// </summary>
    private static void EnsurePermissionIsCustom(Permission permission)
    {
        // Every built-in is referenced by fixed id from code — Administrator by admin routes,
        // Upload File by DocumentEndpoints, Generate Files by the turn's tool composition — so
        // renaming or deleting one would break what depends on it. Driven off Names, which only
        // endpoint-filter ids are forced into at map time; PermissionIdsTests holds the rest.
        if (PermissionIds.Names.TryGetValue(permission.Id, out var builtInName))
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(Permission.Id), $"The built-in {builtInName} permission cannot be modified.")
            ]);
        }

        if (permission.McpServerId is not null)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(Permission.Id), "This permission is managed by its MCP server and cannot be modified directly.")
            ]);
        }
    }

    /// <summary>
    /// Throws when the name is already used by an active permission or an active MCP
    /// server, so callers get a clear 400 instead of an opaque unique-index violation at
    /// save time. Permission and server names share one uniqueness space because every
    /// server auto-creates a same-named permission.
    /// </summary>
    private async Task EnsureNameIsAvailableAsync(string name, Guid? excludePermissionId, CancellationToken cancellationToken)
    {
        var permissionNameTaken = await _ctx.Permissions
            .AnyAsync(p => p.Name == name
                && (excludePermissionId == null || p.Id != excludePermissionId), cancellationToken);

        var serverNameTaken = await _ctx.McpServers
            .AnyAsync(s => s.Name == name && !s.DateDeactivated.HasValue, cancellationToken);

        if (permissionNameTaken || serverNameTaken)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(Permission.Name), $"The name '{name}' is already in use by a permission or MCP server.")
            ]);
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Permission {PermissionId} created by {UserId}")]
    private static partial void LogPermissionCreated(ILogger logger, Guid permissionId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Permission {PermissionId} updated by {UserId}")]
    private static partial void LogPermissionUpdated(ILogger logger, Guid permissionId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Permission {PermissionId} deactivated by {UserId}")]
    private static partial void LogPermissionDeactivated(ILogger logger, Guid permissionId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Permission {PermissionId} granted to user {TargetUserId} by {UserId}")]
    private static partial void LogPermissionGrantedToUser(
        ILogger logger, Guid permissionId, Guid targetUserId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Permission {PermissionId} revoked from user {TargetUserId} by {UserId}")]
    private static partial void LogPermissionRevokedFromUser(
        ILogger logger, Guid permissionId, Guid targetUserId, Guid userId);

    #endregion
}
