using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <summary>
    /// Seeds the catalog row the File Agent runs on.
    /// </summary>
    /// <remarks>
    /// Its own row rather than the summarizer's, so the two can be repointed independently, and
    /// hidden from the chat picker because it is a purpose-built deployment nobody holds a
    /// conversation with. The deployment name is this environment's; an operator repoints it by
    /// editing the row, which the agent reads at the moment of use.
    /// </remarks>
    public partial class SeedFileAgentModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "Core.Ref",
                table: "Model",
                columns: new[] { "Id", "ContextWindowSize", "CreatedById", "DateCreated", "DateDeactivated", "DateModified", "DeploymentName", "Description", "InputPricePerMillionTokens", "IsDefault", "IsReasoningEnabled", "IsToolEnabled", "IsUserSelectable", "MaxOutputTokens", "ModifiedById", "Name", "OutputPricePerMillionTokens", "ProviderId" },
                values: new object[] { new Guid("8f2b4d16-9c05-4a3e-8f7a-1d6a9c2b5e04"), 1000000m, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "rr-gpt5.6-luna", "Runs the File Agent's sandbox turns.", null, false, false, true, false, 16384m, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), "RR GPT 5.6 Luna (File Agent)", null, new Guid("3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0") });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "Core.Ref",
                table: "Model",
                keyColumn: "Id",
                keyValue: new Guid("8f2b4d16-9c05-4a3e-8f7a-1d6a9c2b5e04"));
        }
    }
}
