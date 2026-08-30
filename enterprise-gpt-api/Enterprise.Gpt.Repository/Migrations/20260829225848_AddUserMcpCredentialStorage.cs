using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddUserMcpCredentialStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserMcpCredential",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    McpServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ciphertext = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ApiKeyHint = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    DateRejected = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMcpCredential", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserMcpCredential_McpServer_McpServerId",
                        column: x => x.McpServerId,
                        principalSchema: "Core.Ref",
                        principalTable: "McpServer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserMcpCredential_User_CreatedById",
                        column: x => x.CreatedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserMcpCredential_User_ModifiedById",
                        column: x => x.ModifiedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserMcpCredential_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserMcpCredential_CreatedById",
                schema: "Core",
                table: "UserMcpCredential",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_UserMcpCredential_McpServerId",
                schema: "Core",
                table: "UserMcpCredential",
                column: "McpServerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserMcpCredential_ModifiedById",
                schema: "Core",
                table: "UserMcpCredential",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_UserMcpCredential_UserId_McpServerId",
                schema: "Core",
                table: "UserMcpCredential",
                columns: new[] { "UserId", "McpServerId" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "UserMcpCredential",
                schema: "Core");
        }
    }
}
