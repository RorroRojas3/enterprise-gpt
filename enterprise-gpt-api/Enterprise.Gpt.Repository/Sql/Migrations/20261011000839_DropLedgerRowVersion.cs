using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Sql.Migrations
{
    /// <inheritdoc />
    public partial class DropLedgerRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                schema: "Core.Ref",
                table: "Provider");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "Core",
                table: "ConversationUsageTurn");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "Core",
                table: "ConversationUsageToolCall");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "Core",
                table: "ConversationUsageMcpServer");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "Core",
                table: "ConversationUsage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                schema: "Core.Ref",
                table: "Provider",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                schema: "Core",
                table: "ConversationUsageTurn",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                schema: "Core",
                table: "ConversationUsageToolCall",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                schema: "Core",
                table: "ConversationUsageMcpServer",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                schema: "Core",
                table: "ConversationUsage",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
