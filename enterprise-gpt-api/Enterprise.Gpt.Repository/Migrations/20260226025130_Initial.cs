using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RR.AI_Chat.Repository.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Core.Ref");

            migrationBuilder.EnsureSchema(
                name: "Core");

            migrationBuilder.CreateTable(
                name: "AIService",
                schema: "Core.Ref",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIService", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Conversation",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversation_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Model",
                schema: "Core.Ref",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsToolEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Model", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Model_User_CreatedById",
                        column: x => x.CreatedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Model_User_ModifiedById",
                        column: x => x.ModifiedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConversationDocument",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    MimeType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationDocument", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationDocument_Conversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "Core",
                        principalTable: "Conversation",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationDocument_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConversationDocumentPage",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(1536)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationDocumentPage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationDocumentPage_ConversationDocument_ConversationDocumentId",
                        column: x => x.ConversationDocumentId,
                        principalSchema: "Core",
                        principalTable: "ConversationDocument",
                        principalColumn: "Id");
                });

            migrationBuilder.InsertData(
                schema: "Core.Ref",
                table: "AIService",
                columns: new[] { "Id", "DateCreated", "DateDeactivated", "Name" },
                values: new object[] { new Guid("3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, "AzureAIFoundry" });

            migrationBuilder.InsertData(
                schema: "Core",
                table: "User",
                columns: new[] { "Id", "DateCreated", "DateDeactivated", "DateModified", "Email", "FirstName", "LastName" },
                values: new object[] { new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Enterprise.AI@example.com", "Enterprise", "AI" });

            migrationBuilder.InsertData(
                schema: "Core.Ref",
                table: "Model",
                columns: new[] { "Id", "CreatedById", "DateCreated", "DateDeactivated", "DateModified", "Description", "IsToolEnabled", "ModifiedById", "Name" },
                values: new object[,]
                {
                    { new Guid("0b3948f5-70df-4697-a033-ae70971e1796"), new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "A chat version of GPT-5, optimized for conversational tasks and interactive applications.", true, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), "gpt-5-chat" },
                    { new Guid("c36e22ed-262a-47a1-b2ba-06a38355ae0f"), new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "A mini version of GPT-5, designed for basic tasks and quick responses.", true, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), "gpt-5-mini" },
                    { new Guid("fd01b615-1e9f-46af-957f-e4eaeff02766"), new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "A nano version of GPT-5, optimized for lightweight tasks and minimal resource usage.", true, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), "gpt-5-nano" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversation_UserId",
                schema: "Core",
                table: "Conversation",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocument_ConversationId",
                schema: "Core",
                table: "ConversationDocument",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocument_UserId",
                schema: "Core",
                table: "ConversationDocument",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocumentPage_ConversationDocumentId",
                schema: "Core",
                table: "ConversationDocumentPage",
                column: "ConversationDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Model_CreatedById",
                schema: "Core.Ref",
                table: "Model",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Model_ModifiedById",
                schema: "Core.Ref",
                table: "Model",
                column: "ModifiedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AIService",
                schema: "Core.Ref");

            migrationBuilder.DropTable(
                name: "ConversationDocumentPage",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "Model",
                schema: "Core.Ref");

            migrationBuilder.DropTable(
                name: "ConversationDocument",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "Conversation",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "User",
                schema: "Core");
        }
    }
}
