using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddContextTokensAndModelPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "InputPricePerMillionTokens",
                schema: "Core.Ref",
                table: "Model",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutputPricePerMillionTokens",
                schema: "Core.Ref",
                table: "Model",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContextTokens",
                schema: "Core",
                table: "ConversationUsage",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InputPricePerMillionTokens",
                schema: "Core",
                table: "ConversationUsage",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutputPricePerMillionTokens",
                schema: "Core",
                table: "ConversationUsage",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContextTokens",
                schema: "Core",
                table: "Conversation",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.UpdateData(
                schema: "Core.Ref",
                table: "Model",
                keyColumn: "Id",
                keyValue: new Guid("c36e22ed-262a-47a1-b2ba-06a38355ae0f"),
                columns: new[] { "InputPricePerMillionTokens", "OutputPricePerMillionTokens" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputPricePerMillionTokens",
                schema: "Core.Ref",
                table: "Model");

            migrationBuilder.DropColumn(
                name: "OutputPricePerMillionTokens",
                schema: "Core.Ref",
                table: "Model");

            migrationBuilder.DropColumn(
                name: "ContextTokens",
                schema: "Core",
                table: "ConversationUsage");

            migrationBuilder.DropColumn(
                name: "InputPricePerMillionTokens",
                schema: "Core",
                table: "ConversationUsage");

            migrationBuilder.DropColumn(
                name: "OutputPricePerMillionTokens",
                schema: "Core",
                table: "ConversationUsage");

            migrationBuilder.DropColumn(
                name: "ContextTokens",
                schema: "Core",
                table: "Conversation");
        }
    }
}
