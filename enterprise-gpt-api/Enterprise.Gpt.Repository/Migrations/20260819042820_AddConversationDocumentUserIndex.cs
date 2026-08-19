using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationDocumentUserIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationDocument_UserId",
                schema: "Core",
                table: "ConversationDocument");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocument_UserId_DateDeactivated",
                schema: "Core",
                table: "ConversationDocument",
                columns: new[] { "UserId", "DateDeactivated" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationDocument_UserId_DateDeactivated",
                schema: "Core",
                table: "ConversationDocument");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocument_UserId",
                schema: "Core",
                table: "ConversationDocument",
                column: "UserId");
        }
    }
}
