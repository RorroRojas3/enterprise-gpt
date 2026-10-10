using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Mappers;

namespace Enterprise.Gpt.Service
{
    public interface IMcpServerService
    {
        /// <summary>
        /// Gets the active MCP servers the calling user holds a permission for, ordered by name.
        /// </summary>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The list of permitted servers in the user-facing shape.</returns>
        Task<List<McpDto>> GetPermittedMcpServersAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets every MCP server, active and deactivated, ordered by name. Intended for
        /// administrators managing the server catalog.
        /// </summary>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The list of all servers in the administrative shape.</returns>
        Task<List<McpServerDto>> GetAllMcpServersAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a single active MCP server by id.
        /// </summary>
        /// <param name="id">The server id.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The matching server.</returns>
        /// <exception cref="NotFoundException">No active server with <paramref name="id"/> exists.</exception>
        Task<McpServerDto> GetMcpServerAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a new MCP server after validating the request, together with its linked
        /// permission (same name) in one atomic save. Users must be granted that permission
        /// before they can see or use the server.
        /// </summary>
        /// <param name="request">The creation request.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The created server.</returns>
        /// <exception cref="ValidationException">The request fails validation, or the name is already in use by an active server or permission.</exception>
        Task<McpServerDto> CreateMcpServerAsync(CreateMcpServerActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing active MCP server after validating the request. Renames are
        /// synced to the linked permission, and cached clients for the server are invalidated
        /// so connection changes take effect on the next request.
        /// </summary>
        /// <param name="id">The id of the server to update.</param>
        /// <param name="request">The update request.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The updated server.</returns>
        /// <exception cref="ValidationException">The request fails validation, or the name is already in use by an active server or permission.</exception>
        /// <exception cref="NotFoundException">No active server with <paramref name="id"/> exists.</exception>
        Task<McpServerDto> UpdateMcpServerAsync(Guid id, UpdateMcpServerActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Soft-deletes an MCP server together with its linked permission and every active
        /// grant of that permission, in one atomic save, then invalidates cached clients.
        /// </summary>
        /// <param name="id">The id of the server to deactivate.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <exception cref="NotFoundException">No active server with <paramref name="id"/> exists.</exception>
        Task DeactivateMcpServerAsync(Guid id, CancellationToken cancellationToken = default);
    }

    public class McpServerService(ILogger<McpServerService> logger,
        ITokenService tokenService,
        EnterpriseGptDbContext ctx,
        IValidator<CreateMcpServerActionDto> createValidator,
        IValidator<UpdateMcpServerActionDto> updateValidator,
        IMcpClientCache mcpClientCache,
        IUserPermissionCache permissionCache) : IMcpServerService
    {
        private readonly ILogger<McpServerService> _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly EnterpriseGptDbContext _ctx = ctx;
        private readonly IValidator<CreateMcpServerActionDto> _createValidator = createValidator;
        private readonly IValidator<UpdateMcpServerActionDto> _updateValidator = updateValidator;
        private readonly IMcpClientCache _mcpClientCache = mcpClientCache;
        private readonly IUserPermissionCache _permissionCache = permissionCache;

        /// <inheritdoc />
        public async Task<List<McpDto>> GetPermittedMcpServersAsync(CancellationToken cancellationToken = default)
        {
            var oid = _tokenService.GetOid();

            // Deliberately reads the database rather than IUserPermissionCache: the McpServers query
            // has to run regardless, so the cache would save no round trip — only an EXISTS over an
            // indexed foreign key — while making a user-visible list eventually consistent.
            return await _ctx.McpServers
                .AsNoTracking()
                .Where(s => !s.DateDeactivated.HasValue
                    && s.Permissions.Any(p => !p.DateDeactivated.HasValue
                        && p.UserPermissions.Any(up => up.UserId == oid && !up.DateDeactivated.HasValue)))
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
            await _createValidator.ValidateAndThrowAsync(request, cancellationToken);
            await EnsureNameIsAvailableAsync(request.Name, excludeServerId: null, cancellationToken);

            var oid = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            var newServer = request.FromCreateMcpServerActionDtoToMcpServer();
            newServer.Id = Guid.NewGuid();
            newServer.DateCreated = date;
            newServer.CreatedById = oid;
            newServer.DateModified = date;
            newServer.ModifiedById = oid;

            // The permission is created in the same SaveChangesAsync so a server can never
            // exist without its gate.
            var permission = new Permission
            {
                Id = Guid.NewGuid(),
                Name = newServer.Name,
                Description = $"Access to the {newServer.Name} MCP server.",
                McpServerId = newServer.Id,
                DateCreated = date,
                CreatedById = oid,
                DateModified = date,
                ModifiedById = oid
            };

            _ctx.Add(newServer);
            _ctx.Add(permission);
            await _ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("MCP server {McpServerId} and permission {PermissionId} created by {UserId}",
                newServer.Id, permission.Id, oid);

            return newServer.MapToMcpServerDto(permission.Id);
        }

        /// <inheritdoc />
        public async Task<McpServerDto> UpdateMcpServerAsync(Guid id, UpdateMcpServerActionDto request, CancellationToken cancellationToken = default)
        {
            await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

            var server = await _ctx.McpServers
                .Include(s => s.Permissions.Where(p => !p.DateDeactivated.HasValue))
                .FirstOrDefaultAsync(s => s.Id == id && !s.DateDeactivated.HasValue, cancellationToken)
                ?? throw new NotFoundException("MCP server not found.");

            if (!string.Equals(server.Name, request.Name, StringComparison.Ordinal))
            {
                await EnsureNameIsAvailableAsync(request.Name, excludeServerId: id, cancellationToken);
            }

            var oid = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;
            // A stored key is consent to send it to one endpoint. Repointing the server is
            // therefore as much a reason to discard the keys as dropping the auth type is:
            // without this, editing the URL forwards every user's token to the new host on
            // their next turn, with nothing asked and nothing shown.
            var discardUserApiKeys = server.AuthType == McpAuthTypes.UserApiKey
                && (request.AuthType != McpAuthTypes.UserApiKey
                    || !string.Equals(server.Url, request.Url, StringComparison.OrdinalIgnoreCase));

            request.FromUpdateMcpServerActionDtoToMcpServer(server);
            server.DateModified = date;
            server.ModifiedById = oid;

            // Keep the linked permission's name/description in sync so administrators keep
            // recognizing it in grant listings after a rename.
            var permission = server.Permissions.FirstOrDefault();
            if (permission is not null)
            {
                permission.Name = server.Name;
                permission.Description = $"Access to the {server.Name} MCP server.";
                permission.DateModified = date;
                permission.ModifiedById = oid;
            }

            if (discardUserApiKeys)
            {
                await DeactivateCredentialsAsync(id, oid, date, cancellationToken);
            }

            await _ctx.SaveChangesAsync(cancellationToken);

            // Connection details may have changed; cached clients must not outlive them.
            _mcpClientCache.InvalidateServer(id);

            _logger.LogInformation("MCP server {McpServerId} updated by {UserId}", id, oid);

            return server.MapToMcpServerDto(permission?.Id);
        }

        /// <inheritdoc />
        public async Task DeactivateMcpServerAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var server = await _ctx.McpServers
                .Include(s => s.Permissions.Where(p => !p.DateDeactivated.HasValue))
                    .ThenInclude(p => p.UserPermissions.Where(up => !up.DateDeactivated.HasValue))
                .Include(s => s.UserCredentials.Where(c => !c.DateDeactivated.HasValue))
                .FirstOrDefaultAsync(s => s.Id == id && !s.DateDeactivated.HasValue, cancellationToken)
                ?? throw new NotFoundException("MCP server not found.");

            var oid = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            // Tracked entities so server, permission, grants and stored credentials deactivate in
            // one atomic save, preserving the invariant that an active grant implies an active
            // permission — and leaving no credential behind for a server nobody can select.
            server.DateDeactivated = date;
            server.DateModified = date;
            server.ModifiedById = oid;

            foreach (var credential in server.UserCredentials)
            {
                credential.DateDeactivated = date;
                credential.DateModified = date;
                credential.ModifiedById = oid;
            }

            foreach (var permission in server.Permissions)
            {
                permission.DateDeactivated = date;
                permission.DateModified = date;
                permission.ModifiedById = oid;

                foreach (var grant in permission.UserPermissions)
                {
                    grant.DateDeactivated = date;
                    grant.DateModified = date;
                    grant.ModifiedById = oid;
                }
            }

            // Collected from the already-included graph, so the cascade costs no extra query.
            Guid[] affectedUserIds = [.. server.Permissions
                .SelectMany(p => p.UserPermissions)
                .Select(g => g.UserId)
                .Distinct()];

            await _ctx.SaveChangesAsync(cancellationToken);

            _mcpClientCache.InvalidateServer(id);
            _permissionCache.Invalidate(affectedUserIds);

            _logger.LogInformation("MCP server {McpServerId} deactivated by {UserId}", id, oid);
        }

        /// <summary>
        /// Marks every stored credential for a server deactivated, leaving the save to the caller
        /// so it lands in the same transaction as the change that made them redundant.
        /// </summary>
        private async Task DeactivateCredentialsAsync(
            Guid mcpServerId, Guid oid, DateTimeOffset date, CancellationToken cancellationToken)
        {
            var credentials = await _ctx.UserMcpCredentials
                .Where(c => c.McpServerId == mcpServerId && !c.DateDeactivated.HasValue)
                .ToListAsync(cancellationToken);

            foreach (var credential in credentials)
            {
                credential.DateDeactivated = date;
                credential.DateModified = date;
                credential.ModifiedById = oid;
            }
        }

        /// <summary>
        /// Throws when the name is already used by an active MCP server or an active
        /// permission, so callers get a clear 400 instead of an opaque unique-index violation
        /// at save time. Server and permission names share one uniqueness space because every
        /// server auto-creates a same-named permission.
        /// </summary>
        private async Task EnsureNameIsAvailableAsync(string name, Guid? excludeServerId, CancellationToken cancellationToken)
        {
            var serverNameTaken = await _ctx.McpServers
                .AnyAsync(s => s.Name == name && !s.DateDeactivated.HasValue
                    && (excludeServerId == null || s.Id != excludeServerId), cancellationToken);

            var permissionNameTaken = await _ctx.Permissions
                .AnyAsync(p => p.Name == name && !p.DateDeactivated.HasValue
                    && (excludeServerId == null || p.McpServerId != excludeServerId), cancellationToken);

            if (serverNameTaken || permissionNameTaken)
            {
                throw new ValidationException(
                [
                    new ValidationFailure(nameof(McpServer.Name), $"The name '{name}' is already in use by an MCP server or permission.")
                ]);
            }
        }
    }
}
