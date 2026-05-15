using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RR.AI_Chat.Dto;
using RR.AI_Chat.Dto.Actions.User;
using RR.AI_Chat.Entity;
using RR.AI_Chat.Repository;
using RR.AI_Chat.Service.Exceptions;

namespace RR.AI_Chat.Service
{
    public interface IUserService
    {
        Task CreateUserAsync(CancellationToken cancellationToken);

        Task<UserDto> UpdateUserAsync(UpdateUserActionDto request, CancellationToken cancellationToken);

        Task DeactivateUserAsync(Guid oid, CancellationToken cancellationToken);
    }   

    public class UserService(ILogger<UserService> logger,
        ITokenService tokenService,
        IGraphService graphService,
        AIChatDbContext ctx) : IUserService
    {
        private readonly ILogger<UserService> _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly IGraphService _graphService = graphService;
        private readonly AIChatDbContext _ctx = ctx;

        /// <inheritdoc />
        public async Task CreateUserAsync(CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();

            var userExist = await IsUserInDatabaseAsync(oid, cancellationToken);
            if (userExist)
            {
                return;
            }

            var date = DateTimeOffset.UtcNow;
            var graphUser = await _graphService.GetUserAsync(oid, cancellationToken);
            var newUser = new User
            {
                Id = oid,
                FirstName = graphUser.GivenName!,
                LastName = graphUser.Surname!,
                Email = (graphUser.Mail ?? graphUser.UserPrincipalName)!,
                DateCreated = date,
                DateModified = date
            };

            await _ctx.Users.AddAsync(newUser, cancellationToken);
            await _ctx.SaveChangesAsync(cancellationToken);
        }

        public async Task<UserDto> UpdateUserAsync(UpdateUserActionDto request, CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();

            var user = await _ctx.Users
                        .Where(x => x.Id == oid && !x.DateDeactivated.HasValue)
                        .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException($"User {oid} not found"); ;

            user.FirstName = request.FirstName;
            user.LastName = request.LastName;
            user.Email = request.Email;
            user.DateModified = DateTimeOffset.UtcNow;

            await _ctx.SaveChangesAsync(cancellationToken);
            return new()
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email
            };
        }

        public async Task DeactivateUserAsync(Guid oid, CancellationToken cancellationToken)
        {
            var rows = await _ctx.Users
                        .Where(x => x.Id == oid && !x.DateDeactivated.HasValue)
                        .ExecuteUpdateAsync(update => 
                            update.SetProperty(x => x.DateDeactivated, DateTimeOffset.UtcNow), cancellationToken);
        }
    }
}
