using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Users;
using Enterprise.Gpt.Dto.Pagination;
using Enterprise.Gpt.Dto.Users;
using Enterprise.Gpt.Entity.Permissions;
using Enterprise.Gpt.Entity.Users;
using Enterprise.Gpt.Repository.Sql.Constants;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Extensions;
using Enterprise.Gpt.Service.Security;
using Enterprise.Gpt.Service.Sorting;
using Enterprise.Gpt.Service.Sorting.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Users;

public interface IUserService
{
    /// <summary>Returns the caller's profile, provisioning it from Microsoft Graph on first sign-in.</summary>
    /// <remarks>
    /// A new user gets exactly the <see cref="Permission.IsDefault"/> permissions. A pre-created user shares the
    /// object id, so they take the existing-user path and keep their pre-assigned grants.
    /// </remarks>
    /// <exception cref="ForbiddenException">The caller's account has been deactivated.</exception>
    /// <exception cref="NotFoundException">
    /// The directory has no user for the caller's object id, or that user has no email address.
    /// </exception>
    Task<(UserDto User, bool Created)> GetOrCreateCurrentUserAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches active users by name or email fragment, by last then first name unless an order is requested.
    /// </summary>
    /// <remarks>
    /// <paramref name="permissionId"/> filters before the count, so the total describes the filtered directory.
    /// </remarks>
    /// <exception cref="ValidationException">
    /// <paramref name="permissionId"/> names no active permission, or <paramref name="sort"/> or
    /// <paramref name="dir"/> is not accepted by this listing.
    /// </exception>
    Task<PaginatedResponseDto<UserDto>> SearchUsersAsync(string? name, int skip = 0, int take = 20, Guid? permissionId = null, string? sort = null, string? dir = null, CancellationToken cancellationToken = default);

    /// <summary>Gets an active user by id, which is their Entra object id.</summary>
    /// <exception cref="NotFoundException">No active user with <paramref name="id"/> exists.</exception>
    Task<UserDto> GetUserAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pre-provisions a user by email, resolving object id and name from Graph, with the requested permissions
    /// plus every default.
    /// </summary>
    /// <exception cref="ValidationException">An active user with that email already exists.</exception>
    /// <exception cref="NotFoundException">
    /// The directory has no user (or no address) for that email, or a requested permission does not exist.
    /// </exception>
    Task<UserDto> CreateUserAsync(CreateUserActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a user's profile and replaces their grants with <see cref="UpdateUserActionDto.PermissionIds"/>;
    /// unchanged grants keep their original audit columns.
    /// </summary>
    /// <exception cref="ValidationException">The caller would revoke their own Administrator permission.</exception>
    /// <exception cref="NotFoundException">
    /// No active user with <paramref name="id"/> exists, or a requested permission does not exist.
    /// </exception>
    Task<UserDto> UpdateUserAsync(Guid id, UpdateUserActionDto request, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a user and every active grant they hold, in one atomic save.</summary>
    /// <remarks>
    /// Not a session revocation: routes that only read the token's object id keep serving an issued token until
    /// it expires. Disable the account in Entra ID to cut access immediately.
    /// </remarks>
    /// <exception cref="ValidationException">The caller is attempting to deactivate themselves.</exception>
    /// <exception cref="NotFoundException">No active user with <paramref name="id"/> exists.</exception>
    Task DeactivateUserAsync(Guid id, CancellationToken cancellationToken = default);
}

public partial class UserService(ILogger<UserService> logger,
    ITokenService tokenService,
    IGraphService graphService,
    EnterpriseGptDbContext ctx,
    IUserPermissionCache permissionCache,
    TimeProvider timeProvider) : IUserService
{
    /// <summary>
    /// The wire key for a rejected <c>?permissionId=</c>: the parameter name a filter control owns.
    /// </summary>
    private const string PermissionIdProperty = "permissionId";

    private readonly ILogger<UserService> _logger = logger;
    private readonly ITokenService _tokenService = tokenService;
    private readonly IGraphService _graphService = graphService;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IUserPermissionCache _permissionCache = permissionCache;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public async Task<(UserDto User, bool Created)> GetOrCreateCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var oid = _tokenService.GetOid();

        // Captured before any grant is read, so a revoke committing mid-request cannot be undone
        // by the Set calls below republishing the pre-revoke snapshot.
        var stamp = _permissionCache.CaptureInvalidationStamp();

        var existing = await FindCurrentUserAsync(oid, cancellationToken);
        if (existing is not null)
        {
            if (await ReconcileDefaultPermissionsAsync(oid, cancellationToken))
            {
                existing = await FindCurrentUserAsync(oid, cancellationToken) ?? existing;
            }

            // The UI posts this on every bootstrap, so this warms the cache; it must follow the
            // reconcile re-read so newly granted defaults are included.
            _permissionCache.Set(oid, stamp, existing.Permissions.Select(p => p.Id));

            return (existing, Created: false);
        }

        var graphUser = await _graphService.GetUserAsync(oid, cancellationToken);

        var newUser = new User
        {
            Id = oid,
            FirstName = graphUser.GivenName ?? string.Empty,
            LastName = graphUser.Surname ?? string.Empty,
            Email = ResolveEmail(graphUser, oid)
        };

        // Loaded tracked (not AsNoTracking) so each grant can carry the Permission navigation,
        // letting MapToUserDto resolve names without a second round-trip.
        var defaultPermissions = await _ctx.Permissions
            .Where(p => p.IsDefault)
            .ToListAsync(cancellationToken);

        foreach (var permission in defaultPermissions)
        {
            // Self-granted: EF inserts the User before its grants in one save, so the
            // CreatedById/ModifiedById foreign keys to it resolve.
            AddGrant(newUser, permission, grantedBy: oid);
        }

        _ctx.Add(newUser);

        try
        {
            await _ctx.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Bootstrap calls (and repeated MSAL sign-in events) can race past the existence check
            // and both insert; the loser re-reads the winner's row instead of returning a 500.
            _ctx.ChangeTracker.Clear();
            var raced = await FindCurrentUserAsync(oid, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            _permissionCache.Set(oid, stamp, raced.Permissions.Select(p => p.Id));

            return (raced, Created: false);
        }

        LogUserSelfProvisioned(_logger, oid, defaultPermissions.Count);

        var created = newUser.MapToUserDto();
        _permissionCache.Set(oid, stamp, created.Permissions.Select(p => p.Id));

        return (created, Created: true);
    }

    /// <inheritdoc />
    public async Task<PaginatedResponseDto<UserDto>> SearchUsersAsync(string? name, int skip = 0, int take = 20, Guid? permissionId = null, string? sort = null, string? dir = null, CancellationToken cancellationToken = default)
    {
        // Resolved before any database work, so a misspelt sort field costs no query.
        SortSelection<UserSortKeys>? order = SortParameters.IsDefaultOrder(sort, dir)
            ? null
            : UserSortKeyNames.Resolve(sort, dir);

        // Clamped rather than validated: the paging arguments come straight off the query
        // string, and take = 0 would divide by zero when computing CurrentPage below.
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);

        if (permissionId.HasValue)
        {
            await EnsurePermissionIsFilterableAsync(permissionId.Value, cancellationToken);
        }

        var query = _ctx.Users
            .AsNoTracking()
            .Where(x => !x.DateDeactivated.HasValue);

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(x => EF.Functions.Like(x.FirstName, $"%{name}%")
                || EF.Functions.Like(x.LastName, $"%{name}%")
                || EF.Functions.Like(x.Email, $"%{name}%"));
        }

        // Before the count, so TotalCount describes the filtered directory. A revoked grant must not
        // match here, just as it is hidden in the badge projection.
        if (permissionId.HasValue)
        {
            query = query.Where(x => x.UserPermissions
                .Any(grant => grant.PermissionId == permissionId.Value));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await OrderUsers(query, order)
            .Skip(skip)
            .Take(take)
            .Select(UserMapper.MapToUserDtoExpression)
            .ToListAsync(cancellationToken);

        return new PaginatedResponseDto<UserDto>
        {
            Items = items,
            TotalCount = totalCount,
            PageSize = take,
            CurrentPage = (skip / take) + 1
        };
    }

    /// <inheritdoc />
    public async Task<UserDto> GetUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _ctx.Users
            .AsNoTracking()
            .Where(x => x.Id == id && !x.DateDeactivated.HasValue)
            .Select(UserMapper.MapToUserDtoExpression)
            .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("User not found.");
    }

    /// <inheritdoc />
    public async Task<UserDto> CreateUserAsync(CreateUserActionDto request, CancellationToken cancellationToken = default)
    {
        var graphUser = await _graphService.GetUserByEmailAsync(request.Email, cancellationToken);

        // Keying on the directory object id rather than a generated id is what guarantees the
        // pre-created row is the one GetOrCreateCurrentUserAsync finds at first sign-in.
        if (!Guid.TryParse(graphUser.Id, out var oid))
        {
            throw new NotFoundException("The directory returned a user without a usable object id.");
        }

        // Grants are loaded so the revive path skips ones already held: rows deactivated before grants
        // cascaded can still carry live grants, and re-granting would violate the filtered unique index.
        var existing = await _ctx.Users
            .Include(u => u.UserPermissions)
                .ThenInclude(p => p.Permission)
            .FirstOrDefaultAsync(u => u.Id == oid, cancellationToken);

        if (existing is not null && !existing.DateDeactivated.HasValue)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(CreateUserActionDto.Email), "A user with this email address already exists.")
            ]);
        }

        // Grant the requested permissions plus every default, so a pre-created user is never
        // worse off than one who self-provisions.
        var permissionIds = request.PermissionIds.ToHashSet();
        var permissions = await ResolveGrantablePermissionsAsync(permissionIds, includeDefaults: true, cancellationToken);

        var adminOid = _tokenService.GetOid();

        // The primary key is the object id, so a previously deactivated row must be revived
        // rather than re-inserted.
        var user = existing ?? new User { Id = oid };
        user.FirstName = graphUser.GivenName ?? string.Empty;
        user.LastName = graphUser.Surname ?? string.Empty;
        user.Email = ResolveEmail(graphUser, oid);
        user.DateDeactivated = null;

        if (existing is null)
        {
            _ctx.Add(user);
        }

        var alreadyGranted = user.UserPermissions.Select(p => p.PermissionId).ToHashSet();
        var granted = 0;
        foreach (var permission in permissions.Where(p => !alreadyGranted.Contains(p.Id)))
        {
            AddGrant(user, permission, grantedBy: adminOid);
            granted++;
        }

        await _ctx.SaveChangesOrConflictAsync($"User '{oid}' was changed by another request. Reload it and try again.", cancellationToken);

        // Invalidated rather than Set: safer than trusting the revive diff, and it clears any empty
        // set cached for this user before they were pre-created.
        _permissionCache.Invalidate(oid);

        LogUserPreCreated(_logger, oid, granted, adminOid);

        return user.MapToUserDto();
    }

    /// <inheritdoc />
    public async Task<UserDto> UpdateUserAsync(Guid id, UpdateUserActionDto request, CancellationToken cancellationToken = default)
    {
        var user = await _ctx.Users
            .Include(u => u.UserPermissions)
                .ThenInclude(p => p.Permission)
            .FirstOrDefaultAsync(x => x.Id == id && !x.DateDeactivated.HasValue, cancellationToken)
            ?? throw new NotFoundException("User not found.");

        var oid = _tokenService.GetOid();
        var requestedIds = request.PermissionIds.ToHashSet();

        EnsureAdministratorNotSelfRevoked(id, oid, user, requestedIds);

        var date = _timeProvider.GetUtcNow();

        request.FromUpdateUserActionDtoToUser(user);

        // Diff rather than revoke-and-regrant so unchanged grants keep their original
        // DateCreated/CreatedById, preserving who first granted each permission.
        var currentIds = user.UserPermissions.Select(p => p.PermissionId).ToHashSet();

        foreach (var grant in user.UserPermissions.Where(p => !requestedIds.Contains(p.PermissionId)))
        {
            grant.DateDeactivated = date;
            grant.ModifiedById = oid;
        }

        var idsToGrant = requestedIds.Where(x => !currentIds.Contains(x)).ToHashSet();
        var permissionsToGrant = await ResolveGrantablePermissionsAsync(idsToGrant, includeDefaults: false, cancellationToken);

        foreach (var permission in permissionsToGrant)
        {
            AddGrant(user, permission, grantedBy: oid);
        }

        await _ctx.SaveChangesOrConflictAsync($"User '{id}' was changed by another request. Reload it and try again.", cancellationToken);

        _permissionCache.Invalidate(id);

        LogUserUpdated(_logger, id, oid);

        return user.MapToUserDto();
    }

    /// <inheritdoc />
    public async Task DeactivateUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var oid = _tokenService.GetOid();

        if (id == oid)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(User.Id), "Administrators cannot deactivate their own account.")
            ]);
        }

        var user = await _ctx.Users
            .Include(u => u.UserPermissions)
            .FirstOrDefaultAsync(x => x.Id == id && !x.DateDeactivated.HasValue, cancellationToken)
            ?? throw new NotFoundException("User not found.");

        var date = _timeProvider.GetUtcNow();

        user.DateDeactivated = date;

        // Grants must deactivate with the user: authorization reads only Core.UserPermission, so a
        // live grant would leave a deactivated administrator with full admin access.
        foreach (var grant in user.UserPermissions)
        {
            grant.DateDeactivated = date;
            grant.ModifiedById = oid;
        }

        await _ctx.SaveChangesOrConflictAsync($"User '{id}' was changed by another request. Reload it and try again.", cancellationToken);

        // The cache stores grants, not user state, so this eviction is half of what actually
        // removes a deactivated administrator's access — the grant cascade above is the other.
        _permissionCache.Invalidate(id);

        LogUserDeactivated(_logger, id, oid, user.UserPermissions.Count);
    }

    #region Private methods

    /// <remarks>
    /// A 400 rather than this service's usual 404, so a filter control can show it against its own parameter.
    /// Deactivated permissions are refused too, since <c>GET api/permissions</c> never offers them.
    /// </remarks>
    private async Task EnsurePermissionIsFilterableAsync(Guid permissionId, CancellationToken cancellationToken)
    {
        var exists = await _ctx.Permissions
            .AnyAsync(x => x.Id == permissionId, cancellationToken);

        if (!exists)
        {
            throw new ValidationException(
            [
                new ValidationFailure(
                    PermissionIdProperty,
                    $"'{permissionId}' is not an active permission.")
            ]);
        }
    }

    /// <remarks>
    /// Ordering follows the database collation (case-insensitive on SQL Server, ordinal on the SQLite unit
    /// tests), so a client re-sorting with <c>localeCompare</c> will not agree with it.
    /// </remarks>
    private static IOrderedQueryable<User> OrderUsers(
        IQueryable<User> query,
        SortSelection<UserSortKeys>? order)
    {
        if (order is not { } requested)
        {
            // The original ordering, deliberately without a tie-break: adding Id would reorder
            // namesakes, and existing clients are promised it unchanged.
            return query
                .OrderBy(x => x.LastName)
                .ThenBy(x => x.FirstName);
        }

        var ordered = requested.Key switch
        {
            UserSortKeys.Email => requested.IsAscending
                ? query.OrderBy(x => x.Email)
                : query.OrderByDescending(x => x.Email),
            UserSortKeys.Created => requested.IsAscending
                ? query.OrderBy(x => x.DateCreated)
                : query.OrderByDescending(x => x.DateCreated),
            _ => requested.IsAscending
                ? query.OrderBy(x => x.LastName).ThenBy(x => x.FirstName)
                : query.OrderByDescending(x => x.LastName).ThenByDescending(x => x.FirstName)
        };

        // Requested orders get an Id tie-break so page boundaries are stable. It follows the requested
        // direction, or rows all tied on the sort key would return the same page both ways.
        return requested.IsAscending
            ? ordered.ThenBy(x => x.Id)
            : ordered.ThenByDescending(x => x.Id);
    }

    /// <summary>Returns the caller's profile, or <see langword="null"/> when they must be provisioned.</summary>
    /// <remarks>
    /// A deactivated row still holds the primary key, so it is rejected rather than reported absent, which would
    /// make the caller re-insert its own object id.
    /// </remarks>
    private async Task<UserDto?> FindCurrentUserAsync(Guid oid, CancellationToken cancellationToken)
    {
        var existing = await _ctx.Users
            .AsNoTracking()
            .Where(u => u.Id == oid && !u.DateDeactivated.HasValue)
            .Select(UserMapper.MapToUserDtoExpression)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        // Probed only on a miss, keeping the every-page-load path to one query.
        var isDeactivated = await _ctx.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == oid, cancellationToken);

        return isDeactivated
            ? throw new ForbiddenException("This account has been deactivated.")
            : null;
    }

    /// <summary>Grants any never-held default permission; reports whether anything changed.</summary>
    /// <remarks>
    /// Provisioning alone would never deliver defaults added after a user's first sign-in. A revoked grant counts
    /// as held, so an administrator's revocation is never silently reinstated.
    /// </remarks>
    private async Task<bool> ReconcileDefaultPermissionsAsync(Guid oid, CancellationToken cancellationToken)
    {
        var missing = await FindDefaultPermissionsNeverGrantedIncludingRevokedAsync(oid, cancellationToken);

        if (missing.Count == 0)
        {
            return false;
        }

        foreach (var permissionId in missing)
        {
            // Built without the Permission navigation because there is no tracked User to hang it off
            // here; the caller re-reads the profile afterwards, so nothing depends on the graph.
            _ctx.Add(new UserPermission
            {
                Id = Guid.NewGuid(),
                UserId = oid,
                PermissionId = permissionId,
                CreatedById = oid,
                ModifiedById = oid
            });
        }

        try
        {
            await _ctx.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Racing bootstraps can insert the same grant; the unique index fails the loser, whose row the
            // winner already wrote. Only duplicate keys are swallowed: other faults are real.
            foreach (var entry in _ctx.ChangeTracker.Entries<UserPermission>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            LogDefaultPermissionsRace(_logger, ex, oid);
            return true;
        }

        LogMissingDefaultsGranted(_logger, missing.Count, oid);

        return true;
    }

    /// <remarks>
    /// SQL Server only (2601 unique index, 2627 unique constraint); any other provider's error is rethrown, the
    /// safe direction to be wrong in.
    /// </remarks>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqlException { Number: 2601 or 2627 };
    }

    /// <remarks>The filter is lifted so a revoked grant's soft-deleted row still blocks a re-grant.</remarks>
    private Task<List<Guid>> FindDefaultPermissionsNeverGrantedIncludingRevokedAsync(Guid oid, CancellationToken cancellationToken) =>
        _ctx.Permissions
            .IgnoreQueryFilters([QueryFilterNames.SoftDelete])
            .Where(p => p.IsDefault
                && !p.DateDeactivated.HasValue
                && !p.UserPermissions.Any(up => up.UserId == oid))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// The explicit <c>Add</c> is required: for an existing user EF would infer an update from the set key and
    /// fail the save with a concurrency exception.
    /// </remarks>
    private void AddGrant(User user, Permission permission, Guid grantedBy)
    {
        var grant = new UserPermission
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PermissionId = permission.Id,
            Permission = permission,
            CreatedById = grantedBy,
            ModifiedById = grantedBy
        };

        user.UserPermissions.Add(grant);
        _ctx.Add(grant);
    }

    /// <summary>
    /// Loads the requested (and optionally default) permissions tracked, throwing a precise 404 for a missing or
    /// deactivated id rather than an FK violation at save.
    /// </summary>
    private async Task<List<Permission>> ResolveGrantablePermissionsAsync(
        HashSet<Guid> permissionIds, bool includeDefaults, CancellationToken cancellationToken)
    {
        if (permissionIds.Count == 0 && !includeDefaults)
        {
            return [];
        }

        var permissions = await _ctx.Permissions
            .Where(p => (permissionIds.Contains(p.Id) || (includeDefaults && p.IsDefault)))
            .ToListAsync(cancellationToken);

        // Cast to Guid? so the "nothing missing" case is null rather than Guid.Empty, which is
        // itself a possible (if validator-rejected) input and would otherwise be swallowed.
        var resolvedIds = permissions.Select(p => p.Id).ToHashSet();
        if (permissionIds.Except(resolvedIds).Cast<Guid?>().FirstOrDefault() is { } missingId)
        {
            throw new NotFoundException($"Permission {missingId} not found.");
        }

        return permissions;
    }

    /// <summary>
    /// Refuses a self-revocation of Administrator, since every admin route authorizes against that one grant.
    /// </summary>
    private static void EnsureAdministratorNotSelfRevoked(Guid targetUserId, Guid callerOid, User user, HashSet<Guid> requestedIds)
    {
        if (targetUserId != callerOid)
        {
            return;
        }

        var holdsAdministrator = user.UserPermissions.Any(p => p.PermissionId == PermissionIds.Administrator);
        if (holdsAdministrator && !requestedIds.Contains(PermissionIds.Administrator))
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(UpdateUserActionDto.PermissionIds),
                    "Administrators cannot revoke their own Administrator permission.")
            ]);
        }
    }

    /// <summary>
    /// Prefers <c>mail</c> over <c>userPrincipalName</c> and throws when neither exists. The message names the
    /// object id, not the address, because exception handlers log it and logs must not carry PII.
    /// </summary>
    private static string ResolveEmail(Microsoft.Graph.Models.User graphUser, Guid oid)
    {
        return graphUser.Mail
            ?? graphUser.UserPrincipalName
            ?? throw new NotFoundException($"Directory user {oid} has no email address.");
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} self-provisioned with {DefaultPermissionCount} default permissions")]
    private static partial void LogUserSelfProvisioned(ILogger logger, Guid userId, int defaultPermissionCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {TargetUserId} pre-created with {PermissionCount} permissions by {UserId}")]
    private static partial void LogUserPreCreated(ILogger logger, Guid targetUserId, int permissionCount, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {TargetUserId} updated by {UserId}")]
    private static partial void LogUserUpdated(ILogger logger, Guid targetUserId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {TargetUserId} deactivated by {UserId} along with {GrantCount} grants")]
    private static partial void LogUserDeactivated(ILogger logger, Guid targetUserId, Guid userId, int grantCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Default permissions for user {UserId} were reconciled concurrently by another request")]
    private static partial void LogDefaultPermissionsRace(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Granted {GrantCount} missing default permission(s) to existing user {UserId}")]
    private static partial void LogMissingDefaultsGranted(ILogger logger, int grantCount, Guid userId);

    #endregion
}
