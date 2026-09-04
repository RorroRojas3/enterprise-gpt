using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class RaiseSummarizerOutputCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "Core.Ref",
                table: "Model",
                keyColumn: "Id",
                keyValue: new Guid("c36e22ed-262a-47a1-b2ba-06a38355ae0f"),
                column: "MaxOutputTokens",
                value: 32768m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "Core.Ref",
                table: "Model",
                keyColumn: "Id",
                keyValue: new Guid("c36e22ed-262a-47a1-b2ba-06a38355ae0f"),
                column: "MaxOutputTokens",
                value: 16384m);
        }
    }
}
