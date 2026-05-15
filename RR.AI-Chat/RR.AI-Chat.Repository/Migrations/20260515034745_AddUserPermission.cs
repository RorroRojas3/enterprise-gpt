using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RR.AI_Chat.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserPermission",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<int>(type: "int", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPermission", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPermission_User_CreatedById",
                        column: x => x.CreatedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserPermission_User_ModifiedById",
                        column: x => x.ModifiedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserPermission_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserPermission_CreatedById",
                schema: "Core",
                table: "UserPermission",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermission_ModifiedById",
                schema: "Core",
                table: "UserPermission",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermission_UserId_Permission",
                schema: "Core",
                table: "UserPermission",
                columns: new[] { "UserId", "Permission" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserPermission",
                schema: "Core");
        }
    }
}
