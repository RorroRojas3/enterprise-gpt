using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddModelIsUserSelectable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true backfills every pre-existing row in one statement. The opposite
            // — the CLR default EF would otherwise scaffold — would hide the entire catalog from
            // the chat picker the moment this ran.
            migrationBuilder.AddColumn<bool>(
                name: "IsUserSelectable",
                schema: "Core.Ref",
                table: "Model",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.UpdateData(
                schema: "Core.Ref",
                table: "Model",
                keyColumn: "Id",
                keyValue: new Guid("c36e22ed-262a-47a1-b2ba-06a38355ae0f"),
                column: "IsUserSelectable",
                value: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsUserSelectable",
                schema: "Core.Ref",
                table: "Model");
        }
    }
}
