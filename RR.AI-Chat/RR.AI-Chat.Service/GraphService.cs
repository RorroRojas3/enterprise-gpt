using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using RR.AI_Chat.Service.Exceptions;

namespace RR.AI_Chat.Service
{
    public interface IGraphService
    {
        Task<User> GetUserAsync(Guid oid, CancellationToken cancellationToken);
    }   

    public class GraphService(GraphServiceClient graphServiceClient) : IGraphService
    {
        private readonly GraphServiceClient _graphClient = graphServiceClient;

        public async Task<User> GetUserAsync(Guid oid, CancellationToken cancellationToken)
        {
            var user = await _graphClient.Users[oid.ToString()].GetAsync(requestConfig =>
            {
                requestConfig.QueryParameters.Select = ["givenName", "surname", "mail", "userPrincipalName"];
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            return user ?? throw new NotFoundException($"User {oid} not found");
        }
    }
}
