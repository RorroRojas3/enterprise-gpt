using System.Collections.Concurrent;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Users;
using GraphUser = Microsoft.Graph.Models.User;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// In-memory <see cref="IGraphService"/>. Misses throw <see cref="NotFoundException"/>, matching the
/// production contract behind the 404s under test.
/// </summary>
public sealed class FakeGraphService : IGraphService
{
    private readonly ConcurrentDictionary<Guid, GraphUser> _byOid = new();
    private readonly ConcurrentDictionary<string, GraphUser> _byEmail = new(StringComparer.OrdinalIgnoreCase);

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
