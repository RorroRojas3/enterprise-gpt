using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Mappers;

namespace Enterprise.Gpt.Service
{
    public interface IUserService
    {
        /// <summary>
        /// Returns the calling user's profile, provisioning it from Microsoft Graph on first
        /// sign-in. A newly provisioned user receives exactly the permissions flagged
        /// <see cref="Permission.IsDefault"/>; anything beyond that is granted by an
        /// administrator. A user an administrator pre-created keys on the same object id, so they
        /// take the existing-user path and keep their pre-assigned permissions.
        /// </summary>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The user's profile and whether this call created it.</returns>
        /// <exception cref="ForbiddenException">The caller's account has been deactivated.</exception>
        /// <exception cref="NotFoundException">The directory has no user for the caller's object id, or that directory user has no email address.</exception>
        Task<(UserDto User, bool Created)> GetOrCreateCurrentUserAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Searches active users by name or email, ordered by last name then first name.
        /// </summary>
        /// <param name="name">An optional fragment matched against first name, last name, and email.</param>
        /// <param name="skip">The number of users to skip.</param>
        /// <param name="take">The page size.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>A page of matching users.</returns>
        Task<PaginatedResponseDto<UserDto>> SearchUsersAsync(string? name, int skip = 0, int take = 20, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a single active user by id.
        /// </summary>
        /// <param name="id">The user id, which is the Entra ID object id.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The matching user.</returns>
        /// <exception cref="NotFoundException">No active user with <paramref name="id"/> exists.</exception>
        Task<UserDto> GetUserAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Provisions a user ahead of their first sign-in from their email address, resolving the
        /// object id and display name from Microsoft Graph. The user receives the requested
        /// permissions plus every default permission.
        /// </summary>
        /// <param name="request">The creation request carrying the email and permissions to assign.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The created user.</returns>
        /// <exception cref="ValidationException">The request fails validation, or an active user with that email already exists.</exception>
        /// <exception cref="NotFoundException">The directory has no user with that email, the directory user has no email address, or a requested permission does not exist.</exception>
        Task<UserDto> CreateUserAsync(CreateUserActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates a user's profile and replaces their permission set with
        /// <see cref="UpdateUserActionDto.PermissionIds"/>. Grants absent from the request are
        /// revoked, new ones are added, and unchanged ones keep their original audit columns.
        /// </summary>
        /// <param name="id">The id of the user to update.</param>
        /// <param name="request">The update request.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The updated user.</returns>
        /// <exception cref="ValidationException">The request fails validation, or the caller would revoke their own Administrator permission.</exception>
        /// <exception cref="NotFoundException">No active user with <paramref name="id"/> exists, or a requested permission does not exist.</exception>
        Task<UserDto> UpdateUserAsync(Guid id, UpdateUserActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Soft-deletes a user together with every active grant they hold, in one atomic save.
        /// <para>
        /// This removes administrative access and blocks future sign-in through
        /// <see cref="GetOrCreateCurrentUserAsync"/>, but it is not a full session revocation:
        /// endpoints that only read the object id off the bearer token — conversations, documents,
        /// and chat — keep serving the holder of an already-issued token until it expires.
        /// Disable or delete the account in Entra ID to cut access off immediately.
        /// </para>
        /// </summary>
        /// <param name="id">The id of the user to deactivate.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <exception cref="ValidationException">The caller is attempting to deactivate themselves.</exception>
        /// <exception cref="NotFoundException">No active user with <paramref name="id"/> exists.</exception>
        Task DeactivateUserAsync(Guid id, CancellationToken cancellationToken = default);
    }

    public class UserService(ILogger<UserService> logger,
        ITokenService tokenService,
        IGraphService graphService,
        EnterpriseGptDbContext ctx,
        IValidator<CreateUserActionDto> createValidator,
        IValidator<UpdateUserActionDto> updateValidator) : IUserService
    {
        private readonly ILogger<UserService> _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly IGraphService _graphService = graphService;
        private readonly EnterpriseGptDbContext _ctx = ctx;
        private readonly IValidator<CreateUserActionDto> _createValidator = createValidator;
        private readonly IValidator<UpdateUserActionDto> _updateValidator = updateValidator;

        /// <inheritdoc />
        public async Task<(UserDto User, bool Created)> GetOrCreateCurrentUserAsync(CancellationToken cancellationToken = default)
        {
            var oid = _tokenService.GetOid();

            var existing = await FindCurrentUserAsync(oid, cancellationToken);
            if (existing is not null)
            {
                return (existing, Created: false);
            }

            var graphUser = await _graphService.GetUserAsync(oid, cancellationToken);
            var date = DateTimeOffset.UtcNow;

            var newUser = new User
            {
                Id = oid,
                FirstName = graphUser.GivenName ?? string.Empty,
                LastName = graphUser.Surname ?? string.Empty,
                Email = ResolveEmail(graphUser, oid),
                DateCreated = date,
                DateModified = date
            };

            // Loaded tracked (not AsNoTracking) so each grant can carry the Permission navigation,
            // letting MapToUserDto resolve names without a second round-trip.
            var defaultPermissions = await _ctx.Permissions
                .Where(p => p.IsDefault && !p.DateDeactivated.HasValue)
                .ToListAsync(cancellationToken);

            foreach (var permission in defaultPermissions)
            {
                // The audit columns point at the user being created. EF orders the User insert
                // ahead of its grants in the same SaveChangesAsync, so the self-referencing
                // CreatedById/ModifiedById foreign keys resolve.
                AddGrant(newUser, permission, grantedBy: oid, date);
            }

            _ctx.Add(newUser);

            try
            {
                await _ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // The UI posts this endpoint on every bootstrap and MSAL can emit more than one
                // "signed in" notification per session, so two first-sign-in calls can race past
                // the existence check above and both insert the same primary key. The loser
                // re-reads the winner's row instead of surfacing a duplicate-key 500.
                _ctx.ChangeTracker.Clear();
                var raced = await FindCurrentUserAsync(oid, cancellationToken);
                if (raced is null)
                {
                    throw;
                }

                return (raced, Created: false);
            }

            _logger.LogInformation("User {UserId} self-provisioned with {DefaultPermissionCount} default permissions",
                oid, defaultPermissions.Count);

            return (newUser.MapToUserDto(), Created: true);
        }

        /// <inheritdoc />
        public async Task<PaginatedResponseDto<UserDto>> SearchUsersAsync(string? name, int skip = 0, int take = 20, CancellationToken cancellationToken = default)
        {
            // Clamped rather than validated: the paging arguments come straight off the query
            // string, and take = 0 would divide by zero when computing CurrentPage below.
            skip = Math.Max(skip, 0);
            take = Math.Clamp(take, 1, 100);

            var query = _ctx.Users
                .AsNoTracking()
                .Where(x => !x.DateDeactivated.HasValue);

            if (!string.IsNullOrWhiteSpace(name))
            {
                query = query.Where(x => EF.Functions.Like(x.FirstName, $"%{name}%")
                    || EF.Functions.Like(x.LastName, $"%{name}%")
                    || EF.Functions.Like(x.Email, $"%{name}%"));
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                .OrderBy(x => x.LastName)
                .ThenBy(x => x.FirstName)
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
            await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

            var graphUser = await _graphService.GetUserByEmailAsync(request.Email, cancellationToken);

            // Keying on the directory object id rather than a generated id is what guarantees the
            // pre-created row is the one GetOrCreateCurrentUserAsync finds at first sign-in.
            if (!Guid.TryParse(graphUser.Id, out var oid))
            {
                throw new NotFoundException("The directory returned a user without a usable object id.");
            }

            // Active grants are loaded so the revive path below can skip permissions the user
            // already holds. Rows deactivated before grants cascaded with their user can still
            // carry live grants, and re-granting one would violate the filtered unique index on
            // (UserId, PermissionId) WHERE DateDeactivated IS NULL.
            var existing = await _ctx.Users
                .Include(u => u.UserPermissions.Where(p => !p.DateDeactivated.HasValue))
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
            var date = DateTimeOffset.UtcNow;

            // The primary key is the object id, so a previously deactivated row must be revived
            // rather than re-inserted.
            var user = existing ?? new User { Id = oid, DateCreated = date };
            user.FirstName = graphUser.GivenName ?? string.Empty;
            user.LastName = graphUser.Surname ?? string.Empty;
            user.Email = ResolveEmail(graphUser, oid);
            user.DateDeactivated = null;
            user.DateModified = date;

            if (existing is null)
            {
                _ctx.Add(user);
            }

            var alreadyGranted = user.UserPermissions.Select(p => p.PermissionId).ToHashSet();
            var granted = 0;
            foreach (var permission in permissions.Where(p => !alreadyGranted.Contains(p.Id)))
            {
                AddGrant(user, permission, grantedBy: adminOid, date);
                granted++;
            }

            await _ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("User {TargetUserId} pre-created with {PermissionCount} permissions by {UserId}",
                oid, granted, adminOid);

            return user.MapToUserDto();
        }

        /// <inheritdoc />
        public async Task<UserDto> UpdateUserAsync(Guid id, UpdateUserActionDto request, CancellationToken cancellationToken = default)
        {
            await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

            var user = await _ctx.Users
                .Include(u => u.UserPermissions.Where(p => !p.DateDeactivated.HasValue))
                    .ThenInclude(p => p.Permission)
                .FirstOrDefaultAsync(x => x.Id == id && !x.DateDeactivated.HasValue, cancellationToken)
                ?? throw new NotFoundException("User not found.");

            var oid = _tokenService.GetOid();
            var requestedIds = request.PermissionIds.ToHashSet();

            EnsureAdministratorNotSelfRevoked(id, oid, user, requestedIds);

            var date = DateTimeOffset.UtcNow;

            request.FromUpdateUserActionDtoToUser(user);
            user.DateModified = date;

            // Diff rather than revoke-and-regrant so unchanged grants keep their original
            // DateCreated/CreatedById, preserving who first granted each permission.
            var currentIds = user.UserPermissions.Select(p => p.PermissionId).ToHashSet();

            foreach (var grant in user.UserPermissions.Where(p => !requestedIds.Contains(p.PermissionId)))
            {
                grant.DateDeactivated = date;
                grant.DateModified = date;
                grant.ModifiedById = oid;
            }

            var idsToGrant = requestedIds.Where(x => !currentIds.Contains(x)).ToHashSet();
            var permissionsToGrant = await ResolveGrantablePermissionsAsync(idsToGrant, includeDefaults: false, cancellationToken);

            foreach (var permission in permissionsToGrant)
            {
                AddGrant(user, permission, grantedBy: oid, date);
            }

            // Tracked entities throughout, so the profile edit and the whole permission diff land
            // in one atomic save.
            await _ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("User {TargetUserId} updated by {UserId}", id, oid);

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
                .Include(u => u.UserPermissions.Where(p => !p.DateDeactivated.HasValue))
                .FirstOrDefaultAsync(x => x.Id == id && !x.DateDeactivated.HasValue, cancellationToken)
                ?? throw new NotFoundException("User not found.");

            var date = DateTimeOffset.UtcNow;

            user.DateDeactivated = date;
            user.DateModified = date;

            // Grants must deactivate with the user: AdminEndpointFilter authorizes purely on
            // Core.UserPermission and never checks whether the user is still active, so leaving a
            // deactivated administrator's grant alive would leave them with full admin access.
            foreach (var grant in user.UserPermissions)
            {
                grant.DateDeactivated = date;
                grant.DateModified = date;
                grant.ModifiedById = oid;
            }

            await _ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("User {TargetUserId} deactivated by {UserId} along with {GrantCount} grants",
                id, oid, user.UserPermissions.Count);
        }

        /// <summary>
        /// Returns the caller's profile, or <see langword="null"/> when they have no row yet and
        /// must be provisioned. Deactivated accounts are rejected rather than reported as absent:
        /// the row still occupies the primary key, so treating it as absent would make the caller
        /// re-insert their own object id and fail the save with a duplicate key.
        /// </summary>
        /// <exception cref="ForbiddenException">The caller's account has been deactivated.</exception>
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

            // No active row. The extra probe only runs on that miss, keeping the every-page-load
            // path to a single query, and it is what separates "never signed in" from
            // "deactivated" — the latter still occupies the primary key.
            var isDeactivated = await _ctx.Users
                .AsNoTracking()
                .AnyAsync(u => u.Id == oid, cancellationToken);

            return isDeactivated
                ? throw new ForbiddenException("This account has been deactivated.")
                : null;
        }

        /// <summary>
        /// Creates an active grant of <paramref name="permission"/> for <paramref name="user"/> and
        /// tracks it as an insert. The explicit <c>Add</c> is required, not redundant: when the
        /// user already exists, EF discovers the grant through the collection navigation and infers
        /// its state from the key, which is populated here — so it would be treated as an update of
        /// a row that does not exist and fail the save with a concurrency exception. The grant is
        /// also attached to the navigation so the caller can map the result without re-querying.
        /// </summary>
        private void AddGrant(User user, Permission permission, Guid grantedBy, DateTimeOffset date)
        {
            var grant = new UserPermission
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PermissionId = permission.Id,
                Permission = permission,
                DateCreated = date,
                CreatedById = grantedBy,
                DateModified = date,
                ModifiedById = grantedBy
            };

            user.UserPermissions.Add(grant);
            _ctx.Add(grant);
        }

        /// <summary>
        /// Loads the permissions for <paramref name="permissionIds"/>, optionally unioned with
        /// every default permission, and throws when any requested id is missing or deactivated so
        /// callers get a precise 404 instead of an opaque foreign-key violation at save time.
        /// Entities are tracked so callers can assign them to the <c>Permission</c> navigation.
        /// </summary>
        private async Task<List<Permission>> ResolveGrantablePermissionsAsync(
            HashSet<Guid> permissionIds, bool includeDefaults, CancellationToken cancellationToken)
        {
            if (permissionIds.Count == 0 && !includeDefaults)
            {
                return [];
            }

            var permissions = await _ctx.Permissions
                .Where(p => !p.DateDeactivated.HasValue
                    && (permissionIds.Contains(p.Id) || (includeDefaults && p.IsDefault)))
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
        /// Throws when administrators would strip their own Administrator grant. Every admin route
        /// authorizes against that single grant, so allowing it would let an administrator lock
        /// themselves — and potentially the whole tenant — out of user and permission management.
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
        /// Resolves the directory user's email, preferring <c>mail</c> over
        /// <c>userPrincipalName</c>. Throws rather than persisting a null into the non-nullable
        /// email column when the directory carries neither. The message names the object id
        /// rather than the address, because both exception handlers log it and CLAUDE.md forbids
        /// writing PII to the logs.
        /// </summary>
        private static string ResolveEmail(Microsoft.Graph.Models.User graphUser, Guid oid)
        {
            return graphUser.Mail
                ?? graphUser.UserPrincipalName
                ?? throw new NotFoundException($"Directory user {oid} has no email address.");
        }
    }
}
