using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationUsageToolCallModelIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationUsageToolCall_ModelId",
                schema: "Core",
                table: "ConversationUsageToolCall");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageToolCall_ModelId_DateCreated",
                schema: "Core",
                table: "ConversationUsageToolCall",
                columns: new[] { "ModelId", "DateCreated" },
                filter: "[ModelId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationUsageToolCall_ModelId_DateCreated",
                schema: "Core",
                table: "ConversationUsageToolCall");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageToolCall_ModelId",
                schema: "Core",
                table: "ConversationUsageToolCall",
                column: "ModelId");
        }
    }
}
