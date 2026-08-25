using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <summary>
    /// Corrects the seeded summarizer catalog row and hides it from the chat model picker.
    /// </summary>
    /// <remarks>
    /// A data correction, not a conditional seed: it overwrites these four columns even where an
    /// administrator has already edited them, following <c>ModelConfiguration</c>'s own HasData
    /// precedent of asserting the row's shape outright. The summarization engine reads the window
    /// and the output cap from this row at the moment of use, and both were seeded as zero.
    /// </remarks>
    public partial class CorrectSummarizerModelSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "Core.Ref",
                table: "Model",
                keyColumn: "Id",
                keyValue: new Guid("c36e22ed-262a-47a1-b2ba-06a38355ae0f"),
                columns: new[] { "ContextWindowSize", "DeploymentName", "IsUserSelectable", "MaxOutputTokens" },
                values: new object[] { 1000000m, "rr-gpt5.6-luna", false, 16384m });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "Core.Ref",
                table: "Model",
                keyColumn: "Id",
                keyValue: new Guid("c36e22ed-262a-47a1-b2ba-06a38355ae0f"),
                columns: new[] { "ContextWindowSize", "DeploymentName", "IsUserSelectable", "MaxOutputTokens" },
                values: new object[] { 0m, "rr-gpt-5.6-luna", true, 0m });
        }
    }
}
