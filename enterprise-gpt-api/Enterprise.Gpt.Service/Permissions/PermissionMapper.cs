using System.Linq.Expressions;
using Enterprise.Gpt.Dto.Actions.Permissions;
using Enterprise.Gpt.Dto.Permissions;
using Enterprise.Gpt.Entity.Permissions;

namespace Enterprise.Gpt.Service.Permissions;

public static class PermissionMapper
{
    public static Expression<Func<Permission, PermissionDto>> MapToPermissionDtoExpression { get; } =
        permission => new PermissionDto
        {
            Id = permission.Id,
            Name = permission.Name,
            Description = permission.Description,
            IsDefault = permission.IsDefault,
            McpServerId = permission.McpServerId,
            DateDeactivated = permission.DateDeactivated
        };

    #region Public static methods

    public static PermissionDto MapToPermissionDto(this Permission permission)
    {
        return new PermissionDto
        {
            Id = permission.Id,
            Name = permission.Name,
            Description = permission.Description,
            IsDefault = permission.IsDefault,
            McpServerId = permission.McpServerId,
            DateDeactivated = permission.DateDeactivated
        };
    }

    /// <summary>
    /// Creates an untracked permission; callers set <c>Id</c>, <c>CreatedById</c> and <c>ModifiedById</c>.
    /// </summary>
    public static Permission FromCreatePermissionActionDtoToPermission(this CreatePermissionActionDto dto)
    {
        return new Permission
        {
            Name = dto.Name,
            Description = dto.Description,
            IsDefault = dto.IsDefault
        };
    }

    /// <summary>
    /// Applies the update to a tracked permission in place; callers set <c>ModifiedById</c> and save.
    /// </summary>
    public static void FromUpdatePermissionActionDtoToPermission(this UpdatePermissionActionDto dto, Permission permission)
    {
        permission.Name = dto.Name;
        permission.Description = dto.Description;
        permission.IsDefault = dto.IsDefault;
    }

    #endregion
}
