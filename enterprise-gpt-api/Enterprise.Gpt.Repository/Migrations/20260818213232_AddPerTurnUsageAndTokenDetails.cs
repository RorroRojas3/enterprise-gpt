using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddPerTurnUsageAndTokenDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Iteration",
                schema: "Core",
                table: "ConversationUsageToolCall",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CachedInputTokens",
                schema: "Core",
                table: "ConversationUsage",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReasoningTokens",
                schema: "Core",
                table: "ConversationUsage",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConversationUsageTurn",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationUsageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Iteration = table.Column<int>(type: "int", nullable: false),
                    ResponseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ReportedModelId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: true),
                    CachedInputTokens = table.Column<long>(type: "bigint", nullable: true),
                    ReasoningTokens = table.Column<long>(type: "bigint", nullable: true),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationUsageTurn", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationUsageTurn_ConversationUsage_ConversationUsageId",
                        column: x => x.ConversationUsageId,
                        principalSchema: "Core",
                        principalTable: "ConversationUsage",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageTurn_ConversationUsageId_Iteration",
                schema: "Core",
                table: "ConversationUsageTurn",
                columns: new[] { "ConversationUsageId", "Iteration" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationUsageTurn",
                schema: "Core");

            migrationBuilder.DropColumn(
                name: "Iteration",
                schema: "Core",
                table: "ConversationUsageToolCall");

            migrationBuilder.DropColumn(
                name: "CachedInputTokens",
                schema: "Core",
                table: "ConversationUsage");

            migrationBuilder.DropColumn(
                name: "ReasoningTokens",
                schema: "Core",
                table: "ConversationUsage");
        }
    }
}
