using FluentValidation;
using FluentValidation.Results;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service
{
    public interface IGraphService
    {
        /// <summary>
        /// Retrieves a directory user by their Entra ID object id.
        /// </summary>
        /// <param name="oid">The Entra ID object id of the user.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The matching directory user.</returns>
        /// <exception cref="NotFoundException">The directory has no user with <paramref name="oid"/>.</exception>
        Task<User> GetUserAsync(Guid oid, CancellationToken cancellationToken);

        /// <summary>
        /// Retrieves a directory user by email address, matching against both <c>mail</c> and
        /// <c>userPrincipalName</c>. Used by administrator pre-creation, where the email is the
        /// only known identifier and the object id still has to be resolved.
        /// </summary>
        /// <param name="email">The email address or user principal name to look up.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The matching directory user, including its <c>id</c>.</returns>
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
            // The OData filter is built by string concatenation, so a doubled single quote is the
            // required escape — without it an address containing an apostrophe both breaks the
            // query and opens an injection vector into the filter expression.
            var escapedEmail = email.Replace("'", "''");

            // Top = 2 rather than 1: `mail` is not unique in Entra ID (shared mailboxes,
            // mail-enabled objects, mail/UPN collisions across accounts), and the object id this
            // returns becomes the user row's primary key. Silently taking the first match could
            // bind an administrator's pre-creation to the wrong person.
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

            // Messages omit the address itself: both exception handlers log Message, and
            // CLAUDE.md forbids writing PII to the logs.
            return matches.Count == 1
                ? matches[0]
                : throw new NotFoundException("No directory user found for that email address.");
        }
    }
}
