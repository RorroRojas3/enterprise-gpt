using Enterprise.Gpt.Service.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Enterprise.Gpt.Service.Users;

public interface IGraphService
{
    /// <summary>Retrieves a directory user by Entra object id.</summary>
    /// <exception cref="NotFoundException">The directory has no user with <paramref name="oid"/>.</exception>
    Task<User> GetUserAsync(Guid oid, CancellationToken cancellationToken);

    /// <summary>Retrieves a directory user by address, matching <c>mail</c> or <c>userPrincipalName</c>.</summary>
    /// <exception cref="NotFoundException">The directory has no user with <paramref name="email"/>.</exception>
    /// <exception cref="ValidationException">More than one directory user carries that address.</exception>
    Task<User> GetUserByEmailAsync(string email, CancellationToken cancellationToken);
}

public class GraphService(GraphServiceClient graphServiceClient) : IGraphService
{
    private readonly GraphServiceClient _graphClient = graphServiceClient;

    /// <inheritdoc />
    public async Task<User> GetUserAsync(Guid oid, CancellationToken cancellationToken)
    {
        var user = await _graphClient.Users[oid.ToString()].GetAsync(requestConfig =>
        {
            requestConfig.QueryParameters.Select = ["id", "givenName", "surname", "mail", "userPrincipalName"];
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        return user ?? throw new NotFoundException($"User {oid} not found");
    }

    /// <inheritdoc />
    public async Task<User> GetUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        // The OData filter is concatenated, so doubling single quotes is the required escape and
        // what prevents filter injection.
        var escapedEmail = email.Replace("'", "''");

        // Top = 2: `mail` is not unique in Entra ID, and silently taking the first match could key a
        // pre-created user row to the wrong person's object id.
        var response = await _graphClient.Users.GetAsync(requestConfig =>
        {
            requestConfig.QueryParameters.Filter = $"mail eq '{escapedEmail}' or userPrincipalName eq '{escapedEmail}'";
            requestConfig.QueryParameters.Select = ["id", "givenName", "surname", "mail", "userPrincipalName"];
            requestConfig.QueryParameters.Top = 2;
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        var matches = response?.Value ?? [];
        if (matches.Count > 1)
        {
            throw new ValidationException(
            [
                new ValidationFailure("Email", "That email address matches more than one directory user.")
            ]);
        }

        // Messages omit the address: the exception handlers log Message, and logs must not carry PII.
        return matches.Count == 1
            ? matches[0]
            : throw new NotFoundException("No directory user found for that email address.");
    }
}
