using System.Collections.Concurrent;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using GraphUser = Microsoft.Graph.Models.User;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// In-memory stand-in for <see cref="IGraphService"/>. The real implementation calls Microsoft
/// Graph over the network, so without this substitution every user-provisioning test would issue
/// a live directory request against the fake credentials the factory supplies. Lookups that miss
/// throw <see cref="NotFoundException"/>, matching the production contract that drives the 404
/// responses under test.
/// </summary>
public sealed class FakeGraphService : IGraphService
{
    private readonly ConcurrentDictionary<Guid, GraphUser> _byOid = new();
    private readonly ConcurrentDictionary<string, GraphUser> _byEmail = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a directory user resolvable by both object id and email.
    /// </summary>
    /// <param name="oid">The object id the directory user reports.</param>
    /// <param name="givenName">The given name, or <see langword="null"/> to omit it.</param>
    /// <param name="surname">The surname, or <see langword="null"/> to omit it.</param>
    /// <param name="mail">The mail address, or <see langword="null"/> to omit it.</param>
    /// <param name="userPrincipalName">The UPN used as the email fallback.</param>
    public void AddUser(Guid oid, string? givenName, string? surname, string? mail, string? userPrincipalName = null)
    {
        var user = new GraphUser
        {
            Id = oid.ToString(),
            GivenName = givenName,
            Surname = surname,
            Mail = mail,
            UserPrincipalName = userPrincipalName
        };

        _byOid[oid] = user;
        foreach (var key in new[] { mail, userPrincipalName }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            _byEmail[key!] = user;
        }
    }

    /// <summary>
    /// Clears every registered directory user so tests start from a known-empty directory.
    /// </summary>
    public void Reset()
    {
        _byOid.Clear();
        _byEmail.Clear();
    }

    /// <inheritdoc />
    public Task<GraphUser> GetUserAsync(Guid oid, CancellationToken cancellationToken)
    {
        return _byOid.TryGetValue(oid, out var user)
            ? Task.FromResult(user)
            : throw new NotFoundException($"User {oid} not found");
    }

    /// <inheritdoc />
    public Task<GraphUser> GetUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        return _byEmail.TryGetValue(email, out var user)
            ? Task.FromResult(user)
            : throw new NotFoundException($"No directory user found for '{email}'.");
    }
}
