using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSheets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationDocumentSheet",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SheetIndex = table.Column<int>(type: "int", nullable: false),
                    SheetName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    ColumnCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationDocumentSheet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationDocumentSheet_ConversationDocument_ConversationDocumentId",
                        column: x => x.ConversationDocumentId,
                        principalSchema: "Core",
                        principalTable: "ConversationDocument",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocumentSheet",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SheetIndex = table.Column<int>(type: "int", nullable: false),
                    SheetName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    ColumnCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocumentSheet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocumentSheet_ProjectDocument_ProjectDocumentId",
                        column: x => x.ProjectDocumentId,
                        principalSchema: "Core",
                        principalTable: "ProjectDocument",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConversationDocumentSheetColumn",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationDocumentSheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ColumnIndex = table.Column<int>(type: "int", nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    InferredType = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationDocumentSheetColumn", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationDocumentSheetColumn_ConversationDocumentSheet_ConversationDocumentSheetId",
                        column: x => x.ConversationDocumentSheetId,
                        principalSchema: "Core",
                        principalTable: "ConversationDocumentSheet",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConversationDocumentSheetRow",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationDocumentSheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowIndex = table.Column<int>(type: "int", nullable: false),
                    Cells = table.Column<string>(type: "json", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationDocumentSheetRow", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationDocumentSheetRow_ConversationDocumentSheet_ConversationDocumentSheetId",
                        column: x => x.ConversationDocumentSheetId,
                        principalSchema: "Core",
                        principalTable: "ConversationDocumentSheet",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocumentSheetColumn",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectDocumentSheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ColumnIndex = table.Column<int>(type: "int", nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    InferredType = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocumentSheetColumn", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocumentSheetColumn_ProjectDocumentSheet_ProjectDocumentSheetId",
                        column: x => x.ProjectDocumentSheetId,
                        principalSchema: "Core",
                        principalTable: "ProjectDocumentSheet",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocumentSheetRow",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectDocumentSheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowIndex = table.Column<int>(type: "int", nullable: false),
                    Cells = table.Column<string>(type: "json", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocumentSheetRow", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocumentSheetRow_ProjectDocumentSheet_ProjectDocumentSheetId",
                        column: x => x.ProjectDocumentSheetId,
                        principalSchema: "Core",
                        principalTable: "ProjectDocumentSheet",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocumentSheet_ConversationDocumentId_SheetIndex",
                schema: "Core",
                table: "ConversationDocumentSheet",
                columns: new[] { "ConversationDocumentId", "SheetIndex" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocumentSheetColumn_ConversationDocumentSheetId_ColumnIndex",
                schema: "Core",
                table: "ConversationDocumentSheetColumn",
                columns: new[] { "ConversationDocumentSheetId", "ColumnIndex" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocumentSheetRow_ConversationDocumentSheetId_RowIndex",
                schema: "Core",
                table: "ConversationDocumentSheetRow",
                columns: new[] { "ConversationDocumentSheetId", "RowIndex" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentSheet_ProjectDocumentId_SheetIndex",
                schema: "Core",
                table: "ProjectDocumentSheet",
                columns: new[] { "ProjectDocumentId", "SheetIndex" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentSheetColumn_ProjectDocumentSheetId_ColumnIndex",
                schema: "Core",
                table: "ProjectDocumentSheetColumn",
                columns: new[] { "ProjectDocumentSheetId", "ColumnIndex" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentSheetRow_ProjectDocumentSheetId_RowIndex",
                schema: "Core",
                table: "ProjectDocumentSheetRow",
                columns: new[] { "ProjectDocumentSheetId", "RowIndex" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationDocumentSheetColumn",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ConversationDocumentSheetRow",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ProjectDocumentSheetColumn",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ProjectDocumentSheetRow",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ConversationDocumentSheet",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ProjectDocumentSheet",
                schema: "Core");
        }
    }
}
