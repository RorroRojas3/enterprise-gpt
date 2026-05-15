using System.Linq.Expressions;
using RR.AI_Chat.Dto;
using RR.AI_Chat.Dto.Actions.User;
using RR.AI_Chat.Entity;

namespace RR.AI_Chat.Service.Mappers
{
    public static class UserMapper
    {
        /// <summary>
        /// Maps a materialized <see cref="User"/> entity to <see cref="UserDto"/>.
        /// Caller must have already loaded <see cref="User.UserPermissions"/> with the
        /// <c>DateDeactivated IS NULL</c> filter applied (e.g. filtered <c>Include</c>).
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
                Permissions = [.. user.UserPermissions.Select(p => p.Permission)]
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
                    .Select(p => p.Permission)
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
