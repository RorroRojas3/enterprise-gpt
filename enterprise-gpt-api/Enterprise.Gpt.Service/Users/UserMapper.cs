using System.Linq.Expressions;
using Enterprise.Gpt.Dto.Actions.Users;
using Enterprise.Gpt.Dto.Permissions;
using Enterprise.Gpt.Dto.Users;
using Enterprise.Gpt.Entity.Users;

namespace Enterprise.Gpt.Service.Users;

public static class UserMapper
{
    /// <summary>
    /// Query projection to <see cref="UserDto"/> that keeps only active grants, so no filtered
    /// <c>Include</c> is needed.
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

    #region Public static methods

    /// <summary>
    /// Maps a loaded user, with <see cref="User.UserPermissions"/> and their <c>Permission</c> included, to
    /// <see cref="UserDto"/> with active grants only.
    /// </summary>
    /// <remarks>
    /// Filtered here because a grant diff soft-deletes in place, leaving revoked rows in the tracked collection.
    /// </remarks>
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

    /// <summary>Copies an update onto a tracked user; the caller sets audit columns and saves.</summary>
    public static void FromUpdateUserActionDtoToUser(this UpdateUserActionDto dto, User user)
    {
        user.FirstName = dto.FirstName;
        user.LastName = dto.LastName;
        user.Email = dto.Email;
    }

    #endregion
}
