using System.Linq.Expressions;
using Enterprise.Gpt.Dto.Actions.Permissions;
using Enterprise.Gpt.Dto.Permissions;
using Enterprise.Gpt.Entity.Permissions;

namespace Enterprise.Gpt.Service.Permissions
{
    public static class PermissionMapper
    {
        /// <summary>
        /// Maps a materialized <see cref="Permission"/> entity to <see cref="PermissionDto"/>.
        /// </summary>
        /// <param name="permission">The loaded permission entity.</param>
        /// <returns>The mapped DTO.</returns>
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
        /// LINQ projection from <see cref="Permission"/> to <see cref="PermissionDto"/>
        /// for use inside <c>IQueryable.Select(...)</c>.
        /// </summary>
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

        /// <summary>
        /// Creates a new <see cref="Permission"/> entity from a
        /// <see cref="CreatePermissionActionDto"/>. Callers set <c>Id</c>, <c>CreatedById</c> and
        /// <c>ModifiedById</c>; the audit interceptor stamps the dates.
        /// </summary>
        /// <param name="dto">The action DTO carrying the new values.</param>
        /// <returns>The new, untracked entity.</returns>
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
        /// Applies an <see cref="UpdatePermissionActionDto"/> to an existing
        /// <see cref="Permission"/> entity in place. Callers set <c>ModifiedById</c> and save; the audit
        /// interceptor stamps <c>DateModified</c>.
        /// </summary>
        /// <param name="dto">The action DTO carrying the new values.</param>
        /// <param name="permission">The tracked entity to mutate.</param>
        public static void FromUpdatePermissionActionDtoToPermission(this UpdatePermissionActionDto dto, Permission permission)
        {
            permission.Name = dto.Name;
            permission.Description = dto.Description;
            permission.IsDefault = dto.IsDefault;
        }
    }
}
