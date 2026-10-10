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
using Enterprise.Gpt.Service.Security;

namespace Enterprise.Gpt.Service;

public interface IUserMcpCredentialService
{
    /// <summary>
    /// Stores the calling user's credential for an MCP server, replacing any it already holds,
    /// and evicts that user's cached client so the next request connects with the new one.
    /// </summary>
    /// <param name="mcpServerId">The server the credential is for.</param>
    /// <param name="request">The credential as the user supplied it.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>What the caller may know about the stored credential.</returns>
    /// <exception cref="ValidationException">The credential fails validation, or the server takes no user credential.</exception>
    /// <exception cref="NotFoundException">No active server with <paramref name="mcpServerId"/> exists.</exception>
    /// <exception cref="ForbiddenException">The caller does not hold the server's permission.</exception>
    Task<McpCredentialStatusDto> SaveAsync(
        Guid mcpServerId, SaveMcpCredentialActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the calling user's credential for an MCP server and evicts their cached client.
    /// </summary>
    /// <param name="mcpServerId">The server the credential is for.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="NotFoundException">No active server exists, or the caller holds no credential for it.</exception>
    /// <exception cref="ForbiddenException">The caller does not hold the server's permission.</exception>
    Task DeleteAsync(Guid mcpServerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a server refused a user's stored credential, so it stops being offered.
    /// </summary>
    /// <param name="mcpServerId">The server that refused it.</param>
    /// <param name="userOid">The user whose credential was refused.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <remarks>
    /// Does nothing when no active credential is held, so a race with the user removing theirs
    /// cannot resurrect a row.
    /// </remarks>
    Task MarkRejectedAsync(Guid mcpServerId, Guid userOid, CancellationToken cancellationToken = default);
}

public class UserMcpCredentialService(ILogger<UserMcpCredentialService> logger,
    ITokenService tokenService,
    EnterpriseGptDbContext ctx,
    IValidator<SaveMcpCredentialActionDto> saveValidator,
    IUserSecretProtector protector,
    IMcpClientCache mcpClientCache) : IUserMcpCredentialService
{
    private readonly ILogger<UserMcpCredentialService> _logger = logger;
    private readonly ITokenService _tokenService = tokenService;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IValidator<SaveMcpCredentialActionDto> _saveValidator = saveValidator;
    private readonly IUserSecretProtector _protector = protector;
    private readonly IMcpClientCache _mcpClientCache = mcpClientCache;

    /// <inheritdoc />
    public async Task<McpCredentialStatusDto> SaveAsync(
        Guid mcpServerId, SaveMcpCredentialActionDto request, CancellationToken cancellationToken = default)
    {
        await _saveValidator.ValidateAndThrowAsync(request, cancellationToken);

        var oid = _tokenService.GetOid();
        var server = await LoadPermittedServerAsync(mcpServerId, oid, cancellationToken);

        if (server.AuthType != McpAuthTypes.UserApiKey)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(SaveMcpCredentialActionDto.ApiKey),
                    $"MCP server '{server.Name}' does not take an API key.")
            ]);
        }

        var credential = await UpsertAsync(request.ApiKey, mcpServerId, oid, cancellationToken);

        _mcpClientCache.InvalidateServerForUser(mcpServerId, oid);

        _logger.LogInformation(
            "User {UserId} saved an API key for MCP server {McpServerId}", oid, mcpServerId);

        return credential.MapToMcpCredentialStatusDto(mcpServerId);
    }

    /// <summary>
    /// Writes the user's key, inserting or updating, and retries once when a concurrent save
    /// took the row first.
    /// </summary>
    /// <remarks>
    /// The retry is what keeps a double-clicked Save off the 500 path: the filtered unique
    /// index on <c>(UserId, McpServerId)</c> is the only thing serializing two inserts, and
    /// the loser sees a row it can update rather than a conflict it caused.
    /// </remarks>
    private async Task<UserMcpCredential> UpsertAsync(
        string apiKey, Guid mcpServerId, Guid oid, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var date = DateTimeOffset.UtcNow;
            var credential = await _ctx.UserMcpCredentials
                .FirstOrDefaultAsync(c => c.UserId == oid && c.McpServerId == mcpServerId
                    && !c.DateDeactivated.HasValue, cancellationToken);

            if (credential is null)
            {
                credential = new UserMcpCredential
                {
                    Id = Guid.NewGuid(),
                    UserId = oid,
                    McpServerId = mcpServerId,
                    DateCreated = date,
                    CreatedById = oid
                };

                _ctx.Add(credential);
            }

            credential.Ciphertext = _protector.Protect(oid, mcpServerId, apiKey);
            credential.ApiKeyHint = Hint(apiKey);
            // A save is the user asserting the key is good again, so a stale rejection must
            // not keep the server hidden from them.
            credential.DateRejected = null;
            credential.DateModified = date;
            credential.ModifiedById = oid;

            try
            {
                await _ctx.SaveChangesAsync(cancellationToken);

                return credential;
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                // Detached so the second read is not answered from the change tracker with
                // the very entity that failed to insert.
                _ctx.Entry(credential).State = EntityState.Detached;
            }
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
    {
        var oid = _tokenService.GetOid();
        _ = await LoadPermittedServerAsync(mcpServerId, oid, cancellationToken);

        var credential = await _ctx.UserMcpCredentials
            .FirstOrDefaultAsync(c => c.UserId == oid && c.McpServerId == mcpServerId
                && !c.DateDeactivated.HasValue, cancellationToken)
            ?? throw new NotFoundException("No API key is stored for this MCP server.");

        var date = DateTimeOffset.UtcNow;
        credential.DateDeactivated = date;
        credential.DateModified = date;
        credential.ModifiedById = oid;

        await _ctx.SaveChangesAsync(cancellationToken);

        _mcpClientCache.InvalidateServerForUser(mcpServerId, oid);

        _logger.LogInformation(
            "User {UserId} removed their API key for MCP server {McpServerId}", oid, mcpServerId);
    }

    /// <inheritdoc />
    public async Task MarkRejectedAsync(Guid mcpServerId, Guid userOid, CancellationToken cancellationToken = default)
    {
        var date = DateTimeOffset.UtcNow;

        // ExecuteUpdate rather than load-and-save: this is called mid-turn on the streaming
        // request's own scoped context, and SaveChangesAsync there would flush whatever else
        // that request happens to be tracking.
        var updated = await _ctx.UserMcpCredentials
            .Where(c => c.UserId == userOid && c.McpServerId == mcpServerId
                && !c.DateDeactivated.HasValue && !c.DateRejected.HasValue)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(c => c.DateRejected, date)
                    .SetProperty(c => c.DateModified, date)
                    .SetProperty(c => c.ModifiedById, userOid),
                cancellationToken);

        if (updated == 0)
        {
            return;
        }

        // ExecuteUpdate writes past the change tracker, so a copy this context already holds
        // would answer the next read with the pre-rejection value — and the save that clears a
        // rejection would then find nothing to change.
        foreach (var entry in _ctx.ChangeTracker
            .Entries<UserMcpCredential>()
            .Where(e => e.Entity.UserId == userOid && e.Entity.McpServerId == mcpServerId)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }

        _logger.LogInformation(
            "MCP server {McpServerId} rejected the API key of user {UserId}", mcpServerId, userOid);
    }

    /// <summary>
    /// Loads an active server the caller is permitted to use.
    /// </summary>
    /// <remarks>
    /// Reads the database rather than <c>IUserPermissionCache</c> for the reason
    /// <c>McpToolProvider</c> gives: storing a credential against a server is as much a use of
    /// it as calling one, so a revoked grant has to take effect on the next request.
    /// </remarks>
    private async Task<McpServer> LoadPermittedServerAsync(
        Guid mcpServerId, Guid oid, CancellationToken cancellationToken)
    {
        var result = await _ctx.McpServers
            .AsNoTracking()
            .Where(s => s.Id == mcpServerId && !s.DateDeactivated.HasValue)
            .Select(s => new
            {
                Server = s,
                IsPermitted = s.Permissions.Any(p => !p.DateDeactivated.HasValue
                    && p.UserPermissions.Any(up => up.UserId == oid && !up.DateDeactivated.HasValue))
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("MCP server not found.");

        return result.IsPermitted
            ? result.Server
            : throw new ForbiddenException($"You do not have permission to use MCP server '{result.Server.Name}'.");
    }

    /// <summary>
    /// The last four characters of a credential, which is all any route ever discloses.
    /// </summary>
    /// <remarks>
    /// Empty rather than the whole value for anything shorter, so the defensive branch of a
    /// function that feeds <c>GET api/mcps</c> cannot disclose a credential. The validator's
    /// minimum length makes it unreachable; that is not a reason for it to fail open.
    /// </remarks>
    private static string Hint(string apiKey)
    {
        return apiKey.Length <= 4 ? string.Empty : apiKey[^4..];
    }
}
