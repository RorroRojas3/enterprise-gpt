using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Mappers;

namespace Enterprise.Gpt.Service
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
        EnterpriseGptDbContext ctx) : IUserService
    {
        private readonly ILogger<UserService> _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly IGraphService _graphService = graphService;
        private readonly EnterpriseGptDbContext _ctx = ctx;

        /// <inheritdoc />
        public async Task<(UserDto User, bool Created)> CreateUserAsync(CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();

            var existing = await _ctx.Users
                .Where(u => u.Id == oid && !u.DateDeactivated.HasValue)
                .Select(UserMapper.MapToUserDtoExpression)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                return (existing, Created: false);
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

            _ctx.Add(newUser);
            await _ctx.SaveChangesAsync(cancellationToken);

            return (newUser.MapToUserDto(), Created: true);
        }

        /// <inheritdoc />
        public async Task<UserDto> UpdateUserAsync(UpdateUserActionDto request, CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();

            var user = await _ctx.Users
                        .Include(u => u.UserPermissions.Where(p => !p.DateDeactivated.HasValue))
                            .ThenInclude(p => p.Permission)
                        .FirstOrDefaultAsync(x => x.Id == oid && !x.DateDeactivated.HasValue, cancellationToken)
                ?? throw new NotFoundException($"User {oid} not found");

            request.FromUpdateUserActionDtoToUser(user);
            user.DateModified = DateTimeOffset.UtcNow;

            await _ctx.SaveChangesAsync(cancellationToken);
            return user.MapToUserDto();
        }

        /// <inheritdoc />
        public async Task DeactivateUserAsync(Guid oid, CancellationToken cancellationToken)
        {
            var rows = await _ctx.Users
                        .Where(x => x.Id == oid && !x.DateDeactivated.HasValue)
                        .ExecuteUpdateAsync(update =>
                            update.SetProperty(x => x.DateDeactivated, DateTimeOffset.UtcNow), cancellationToken);
        }
    }
}
