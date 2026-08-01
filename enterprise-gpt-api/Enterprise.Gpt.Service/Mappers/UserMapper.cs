using System.Linq.Expressions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Service.Mappers
{
    public static class UserMapper
    {
        /// <summary>
        /// Maps a materialized <see cref="User"/> entity to <see cref="UserDto"/>, projecting only
        /// active grants. Caller must have loaded <see cref="User.UserPermissions"/> with the
        /// <c>Permission</c> navigation populated (e.g. <c>Include(...).ThenInclude(p =&gt; p.Permission)</c>).
        /// The active filter is applied here rather than left to the caller because permission
        /// mutations soft-delete in place: after a grant diff the tracked collection holds both
        /// freshly revoked and freshly added rows, and the revoked ones must not surface.
        /// </summary>
        /// <param name="user">The loaded user entity.</param>
        /// <returns>The mapped DTO.</returns>
        public static UserDto MapToUserDto(this User user)
        {
            return new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                Permissions = [.. user.UserPermissions
                    .Where(p => !p.DateDeactivated.HasValue)
                    .Select(p => new PermissionDto
                    {
                        Id = p.PermissionId,
                        Name = p.Permission.Name,
                        Description = p.Permission.Description,
                        IsDefault = p.Permission.IsDefault,
                        McpServerId = p.Permission.McpServerId
                    })]
            };
        }

        /// <summary>
        /// LINQ projection from <see cref="User"/> to <see cref="UserDto"/> for use inside
        /// <c>IQueryable.Select(...)</c>. Filters permissions to active grants in the same
        /// query so callers don't need a separate filtered <c>Include</c>.
        /// </summary>
        public static Expression<Func<User, UserDto>> MapToUserDtoExpression { get; } =
            user => new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                Permissions = user.UserPermissions
                    .Where(p => !p.DateDeactivated.HasValue)
                    .Select(p => new PermissionDto
                    {
                        Id = p.PermissionId,
                        Name = p.Permission.Name,
                        Description = p.Permission.Description,
                        IsDefault = p.Permission.IsDefault,
                        McpServerId = p.Permission.McpServerId
                    })
                    .ToList()
            };

        /// <summary>
        /// Applies an <see cref="UpdateUserActionDto"/> to an existing <see cref="User"/>
        /// entity in place. Callers are responsible for setting audit columns
        /// (e.g. <c>DateModified</c>) and saving changes.
        /// </summary>
        /// <param name="dto">The action DTO carrying the new values.</param>
        /// <param name="user">The tracked entity to mutate.</param>
        public static void FromUpdateUserActionDtoToUser(this UpdateUserActionDto dto, User user)
        {
            user.FirstName = dto.FirstName;
            user.LastName = dto.LastName;
            user.Email = dto.Email;
        }
    }
}
