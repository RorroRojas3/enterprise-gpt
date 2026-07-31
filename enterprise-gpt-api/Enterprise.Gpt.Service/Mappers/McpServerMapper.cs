using System.Linq.Expressions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Service.Mappers
{
    public static class McpServerMapper
    {
        /// <summary>
        /// Maps a materialized <see cref="McpServer"/> entity to the administrative
        /// <see cref="McpServerDto"/>.
        /// </summary>
        /// <param name="server">The loaded server entity.</param>
        /// <param name="permissionId">The id of the server's active linked permission, when loaded.</param>
        /// <returns>The mapped DTO.</returns>
        public static McpServerDto MapToMcpServerDto(this McpServer server, Guid? permissionId = null)
        {
            return new McpServerDto
            {
                Id = server.Id,
                Name = server.Name,
                Description = server.Description,
                Url = server.Url,
                AuthType = server.AuthType,
                Scope = server.Scope,
                PermissionId = permissionId,
                DateDeactivated = server.DateDeactivated
            };
        }

        /// <summary>
        /// LINQ projection from <see cref="McpServer"/> to <see cref="McpServerDto"/> for use
        /// inside <c>IQueryable.Select(...)</c>. Resolves the active linked permission id in
        /// the same query.
        /// </summary>
        public static Expression<Func<McpServer, McpServerDto>> MapToMcpServerDtoExpression { get; } =
            server => new McpServerDto
            {
                Id = server.Id,
                Name = server.Name,
                Description = server.Description,
                Url = server.Url,
                AuthType = server.AuthType,
                Scope = server.Scope,
                PermissionId = server.Permissions
                    .Where(p => !p.DateDeactivated.HasValue)
                    .Select(p => (Guid?)p.Id)
                    .FirstOrDefault(),
                DateDeactivated = server.DateDeactivated
            };

        /// <summary>
        /// LINQ projection from <see cref="McpServer"/> to the user-facing <see cref="McpDto"/>
        /// for use inside <c>IQueryable.Select(...)</c>.
        /// </summary>
        public static Expression<Func<McpServer, McpDto>> MapToMcpDtoExpression { get; } =
            server => new McpDto
            {
                Id = server.Id,
                Name = server.Name,
                Description = server.Description
            };

        /// <summary>
        /// Creates a new <see cref="McpServer"/> entity from a <see cref="CreateMcpServerActionDto"/>.
        /// Callers are responsible for setting <c>Id</c> and the audit columns
        /// (e.g. <c>DateCreated</c>, <c>CreatedById</c>) before saving.
        /// </summary>
        /// <param name="dto">The action DTO carrying the new values.</param>
        /// <returns>The new, untracked entity.</returns>
        public static McpServer FromCreateMcpServerActionDtoToMcpServer(this CreateMcpServerActionDto dto)
        {
            return new McpServer
            {
                Name = dto.Name,
                Description = dto.Description,
                Url = dto.Url,
                AuthType = dto.AuthType,
                Scope = dto.Scope
            };
        }

        /// <summary>
        /// Applies an <see cref="UpdateMcpServerActionDto"/> to an existing <see cref="McpServer"/>
        /// entity in place. Callers are responsible for setting audit columns
        /// (e.g. <c>DateModified</c>) and saving changes.
        /// </summary>
        /// <param name="dto">The action DTO carrying the new values.</param>
        /// <param name="server">The tracked entity to mutate.</param>
        public static void FromUpdateMcpServerActionDtoToMcpServer(this UpdateMcpServerActionDto dto, McpServer server)
        {
            server.Name = dto.Name;
            server.Description = dto.Description;
            server.Url = dto.Url;
            server.AuthType = dto.AuthType;
            server.Scope = dto.Scope;
        }
    }
}
