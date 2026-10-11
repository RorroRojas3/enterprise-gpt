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
    /// <summary>Gets all active permissions, ordered by name.</summary>
    Task<List<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets every permission, deactivated included, ordered by name.</summary>
    Task<List<PermissionDto>> GetAllPermissionsIncludingDeactivatedAsync(CancellationToken cancellationToken = default);

    /// <exception cref="NotFoundException">No active permission with <paramref name="id"/> exists.</exception>
    Task<PermissionDto> GetPermissionAsync(Guid id, CancellationToken cancellationToken = default);

    /// <exception cref="ValidationException">The name is already in use by an active permission or MCP server.</exception>
    Task<PermissionDto> CreatePermissionAsync(CreatePermissionActionDto request, CancellationToken cancellationToken = default);

    /// <exception cref="ValidationException">The permission is built-in or MCP-managed, or the name is in use.</exception>
    /// <exception cref="NotFoundException">No active permission with <paramref name="id"/> exists.</exception>
    Task<PermissionDto> UpdatePermissionAsync(Guid id, UpdatePermissionActionDto request, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a custom permission and its active grants in one save.</summary>
    /// <exception cref="ValidationException">The permission is built-in or MCP-managed.</exception>
    /// <exception cref="NotFoundException">No active permission with <paramref name="id"/> exists.</exception>
    Task DeactivatePermissionAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Idempotent; a previously revoked grant is re-established with a fresh row.</summary>
    /// <exception cref="NotFoundException">The user or the permission does not exist or is deactivated.</exception>
    Task GrantPermissionAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes the user's active grant.</summary>
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

        // One atomic save keeps "active grant implies active permission".
        permission.DateDeactivated = date;
        permission.ModifiedById = oid;

        foreach (var grant in permission.UserPermissions)
        {
            grant.DateDeactivated = date;
            grant.ModifiedById = oid;
        }

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

        // The filtered unique index covers only active rows, so a re-grant inserts and revoked history stays.
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

        // Mirrors UserService.UpdateUserAsync: both routes can revoke this grant, so both block self-lockout.
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

        _permissionCache.Invalidate(userId);

        LogPermissionRevokedFromUser(_logger, permissionId, userId, oid);
    }

    #region Private methods

    // Built-in and MCP-managed permissions follow their own lifecycles and must not be edited directly.
    private static void EnsurePermissionIsCustom(Permission permission)
    {
        // Built-ins are referenced by fixed id from code, so renaming or deleting one breaks its callers.
        // Names lists them all: endpoint-filter ids are forced in at map time, PermissionIdsTests the rest.
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

    // A clear 400 rather than a unique-index violation at save. Permission and server names share one
    // space because every server creates a same-named permission.
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
