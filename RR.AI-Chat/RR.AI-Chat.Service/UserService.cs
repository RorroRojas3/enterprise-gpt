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
        Task<(UserDto User, bool Created)> CreateUserAsync(CancellationToken cancellationToken);

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
        public async Task<(UserDto User, bool Created)> CreateUserAsync(CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();

            var existing = await _ctx.Users
                .Include(u => u.UserPermissions.Where(p => !p.DateDeactivated.HasValue))
                .FirstOrDefaultAsync(u => u.Id == oid && !u.DateDeactivated.HasValue, cancellationToken);

            if (existing is not null)
            {
                return (MapToDto(existing), Created: false);
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

            _ctx.Users.Add(newUser);
            await _ctx.SaveChangesAsync(cancellationToken);

            return (MapToDto(newUser), Created: true);
        }

        /// <inheritdoc />
        public async Task<UserDto> UpdateUserAsync(UpdateUserActionDto request, CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();

            var user = await _ctx.Users
                        .Include(u => u.UserPermissions.Where(p => !p.DateDeactivated.HasValue))
                        .FirstOrDefaultAsync(x => x.Id == oid && !x.DateDeactivated.HasValue, cancellationToken)
                ?? throw new NotFoundException($"User {oid} not found");

            user.FirstName = request.FirstName;
            user.LastName = request.LastName;
            user.Email = request.Email;
            user.DateModified = DateTimeOffset.UtcNow;

            await _ctx.SaveChangesAsync(cancellationToken);
            return MapToDto(user);
        }

        /// <inheritdoc />
        public async Task DeactivateUserAsync(Guid oid, CancellationToken cancellationToken)
        {
            var rows = await _ctx.Users
                        .Where(x => x.Id == oid && !x.DateDeactivated.HasValue)
                        .ExecuteUpdateAsync(update =>
                            update.SetProperty(x => x.DateDeactivated, DateTimeOffset.UtcNow), cancellationToken);
        }

        #region Private methods

        private static UserDto MapToDto(User user)
        {
            return new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                Permissions = user.UserPermissions
                    .Select(p => p.Permission)
                    .ToList()
            };
        }

        #endregion
    }
}
