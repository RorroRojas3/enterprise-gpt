using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <summary>
    /// Seeds the permission that gates file generation.
    /// </summary>
    /// <remarks>
    /// Not <c>IsDefault</c>, so no existing user picks it up on their next sign-in: every run it
    /// permits provisions a billed sandbox session, and administrators do not hold it implicitly.
    /// <c>Down</c> succeeds only while nobody has been granted it — a revoked grant is soft-deleted
    /// rather than removed and its foreign key is <c>NoAction</c> — so rolling this back on a
    /// deployment that used the feature means clearing those rows first.
    /// </remarks>
    public partial class SeedGenerateFilesPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "Core",
                table: "Permission",
                columns: new[] { "Id", "CreatedById", "DateCreated", "DateDeactivated", "DateModified", "Description", "IsDefault", "McpServerId", "ModifiedById", "Name" },
                values: new object[] { new Guid("c2d3e4f5-a6b7-4c8d-9e0f-1a2b3c4d5e6f"), new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Ask the assistant to create, edit, compare and convert documents.", false, null, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), "Generate Files" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "Core",
                table: "Permission",
                keyColumn: "Id",
                keyValue: new Guid("c2d3e4f5-a6b7-4c8d-9e0f-1a2b3c4d5e6f"));
        }
    }
}
