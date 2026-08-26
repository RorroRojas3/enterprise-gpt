using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationDocumentSummary",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeploymentName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ModelCallCount = table.Column<int>(type: "int", nullable: false),
                    MapUnitCount = table.Column<int>(type: "int", nullable: false),
                    CollapsePasses = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationDocumentSummary", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationDocumentSummary_ConversationDocument_ConversationDocumentId",
                        column: x => x.ConversationDocumentId,
                        principalSchema: "Core",
                        principalTable: "ConversationDocument",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationDocumentSummary_Model_ModelId",
                        column: x => x.ModelId,
                        principalSchema: "Core.Ref",
                        principalTable: "Model",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocumentSummary",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeploymentName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ModelCallCount = table.Column<int>(type: "int", nullable: false),
                    MapUnitCount = table.Column<int>(type: "int", nullable: false),
                    CollapsePasses = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocumentSummary", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocumentSummary_Model_ModelId",
                        column: x => x.ModelId,
                        principalSchema: "Core.Ref",
                        principalTable: "Model",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProjectDocumentSummary_ProjectDocument_ProjectDocumentId",
                        column: x => x.ProjectDocumentId,
                        principalSchema: "Core",
                        principalTable: "ProjectDocument",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocumentSummary_ConversationDocumentId",
                schema: "Core",
                table: "ConversationDocumentSummary",
                column: "ConversationDocumentId",
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocumentSummary_ModelId",
                schema: "Core",
                table: "ConversationDocumentSummary",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentSummary_ModelId",
                schema: "Core",
                table: "ProjectDocumentSummary",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentSummary_ProjectDocumentId",
                schema: "Core",
                table: "ProjectDocumentSummary",
                column: "ProjectDocumentId",
                unique: true,
                filter: "[DateDeactivated] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationDocumentSummary",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ProjectDocumentSummary",
                schema: "Core");
        }
    }
}
