using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Core");

            migrationBuilder.EnsureSchema(
                name: "Core.Ref");

            migrationBuilder.CreateTable(
                name: "Provider",
                schema: "Core.Ref",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Provider", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpServer",
                schema: "Core.Ref",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    AuthType = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpServer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpServer_User_CreatedById",
                        column: x => x.CreatedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_McpServer_User_ModifiedById",
                        column: x => x.ModifiedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Model",
                schema: "Core.Ref",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DeploymentName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ContextWindowSize = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MaxOutputTokens = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsToolEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Model", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Model_Provider_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "Core.Ref",
                        principalTable: "Provider",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Model_User_CreatedById",
                        column: x => x.CreatedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Model_User_ModifiedById",
                        column: x => x.ModifiedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Project",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Project", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Project_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Permission",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    McpServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permission", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Permission_McpServer_McpServerId",
                        column: x => x.McpServerId,
                        principalSchema: "Core.Ref",
                        principalTable: "McpServer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Permission_User_CreatedById",
                        column: x => x.CreatedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Permission_User_ModifiedById",
                        column: x => x.ModifiedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Conversation",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IsFavorite = table.Column<bool>(type: "bit", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversation_Model_ModelId",
                        column: x => x.ModelId,
                        principalSchema: "Core.Ref",
                        principalTable: "Model",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Conversation_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "Core",
                        principalTable: "Project",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Conversation_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocument",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    MimeType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocument", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocument_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "Core",
                        principalTable: "Project",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProjectDocument_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "UserPermission",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPermission", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPermission_Permission_PermissionId",
                        column: x => x.PermissionId,
                        principalSchema: "Core",
                        principalTable: "Permission",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserPermission_User_CreatedById",
                        column: x => x.CreatedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserPermission_User_ModifiedById",
                        column: x => x.ModifiedById,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserPermission_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConversationDocument",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    MimeType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationDocument", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationDocument_Conversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "Core",
                        principalTable: "Conversation",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationDocument_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConversationUsage",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeploymentName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ToolInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ToolOutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    AssistantMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationUsage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationUsage_Conversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "Core",
                        principalTable: "Conversation",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationUsage_Model_ModelId",
                        column: x => x.ModelId,
                        principalSchema: "Core.Ref",
                        principalTable: "Model",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationUsage_Provider_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "Core.Ref",
                        principalTable: "Provider",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationUsage_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "Core",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocumentChunk",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Index = table.Column<int>(type: "int", nullable: false),
                    SourceNumber = table.Column<int>(type: "int", nullable: true),
                    TokenCount = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(1536)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocumentChunk", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocumentChunk_ProjectDocument_ProjectDocumentId",
                        column: x => x.ProjectDocumentId,
                        principalSchema: "Core",
                        principalTable: "ProjectDocument",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConversationDocumentChunk",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    DateModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Index = table.Column<int>(type: "int", nullable: false),
                    SourceNumber = table.Column<int>(type: "int", nullable: true),
                    TokenCount = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(1536)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationDocumentChunk", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationDocumentChunk_ConversationDocument_ConversationDocumentId",
                        column: x => x.ConversationDocumentId,
                        principalSchema: "Core",
                        principalTable: "ConversationDocument",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConversationUsageMcpServer",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationUsageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    McpServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    McpServerName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationUsageMcpServer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationUsageMcpServer_ConversationUsage_ConversationUsageId",
                        column: x => x.ConversationUsageId,
                        principalSchema: "Core",
                        principalTable: "ConversationUsage",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationUsageMcpServer_McpServer_McpServerId",
                        column: x => x.McpServerId,
                        principalSchema: "Core.Ref",
                        principalTable: "McpServer",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConversationUsageToolCall",
                schema: "Core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationUsageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Depth = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ToolName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CallId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    McpServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeploymentName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: true),
                    SubtreeTotalTokens = table.Column<long>(type: "bigint", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateDeactivated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationUsageToolCall", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationUsageToolCall_ConversationUsageToolCall_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "Core",
                        principalTable: "ConversationUsageToolCall",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationUsageToolCall_ConversationUsage_ConversationUsageId",
                        column: x => x.ConversationUsageId,
                        principalSchema: "Core",
                        principalTable: "ConversationUsage",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationUsageToolCall_McpServer_McpServerId",
                        column: x => x.McpServerId,
                        principalSchema: "Core.Ref",
                        principalTable: "McpServer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationUsageToolCall_Model_ModelId",
                        column: x => x.ModelId,
                        principalSchema: "Core.Ref",
                        principalTable: "Model",
                        principalColumn: "Id");
                });

            migrationBuilder.InsertData(
                schema: "Core.Ref",
                table: "Provider",
                columns: new[] { "Id", "DateCreated", "DateDeactivated", "Name" },
                values: new object[,]
                {
                    { new Guid("3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, "AzureOpenAI" },
                    { new Guid("6f93ec17-e981-409f-a523-700584f1e7d6"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, "Anthropic" },
                    { new Guid("8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, "AmazonBedrock" }
                });

            migrationBuilder.InsertData(
                schema: "Core",
                table: "User",
                columns: new[] { "Id", "DateCreated", "DateDeactivated", "DateModified", "Email", "FirstName", "LastName" },
                values: new object[] { new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Enterprise.AI@example.com", "Enterprise", "AI" });

            migrationBuilder.InsertData(
                schema: "Core.Ref",
                table: "Model",
                columns: new[] { "Id", "ContextWindowSize", "CreatedById", "DateCreated", "DateDeactivated", "DateModified", "DeploymentName", "Description", "IsDefault", "IsToolEnabled", "MaxOutputTokens", "ModifiedById", "Name", "ProviderId" },
                values: new object[] { new Guid("c36e22ed-262a-47a1-b2ba-06a38355ae0f"), 0m, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "rr-gpt-5.6-luna", "OpenAI's GPT-5.6 Luna model.", false, true, 0m, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), "RR GPT 5.6 Luna", new Guid("3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0") });

            migrationBuilder.InsertData(
                schema: "Core",
                table: "Permission",
                columns: new[] { "Id", "CreatedById", "DateCreated", "DateDeactivated", "DateModified", "Description", "IsDefault", "McpServerId", "ModifiedById", "Name" },
                values: new object[,]
                {
                    { new Guid("a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d"), new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Full administrative access.", false, null, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), "Administrator" },
                    { new Guid("b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e"), new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Upload documents into a conversation or a project.", true, null, new Guid("5f7ab694-1b6c-4b19-badd-c82b65e794cf"), "Upload File" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversation_ModelId",
                schema: "Core",
                table: "Conversation",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversation_ProjectId_DateDeactivated",
                schema: "Core",
                table: "Conversation",
                columns: new[] { "ProjectId", "DateDeactivated" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversation_UserId_DateDeactivated_DateCreated",
                schema: "Core",
                table: "Conversation",
                columns: new[] { "UserId", "DateDeactivated", "DateCreated" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Conversation_UserId_DateDeactivated_IsFavorite_DateCreated",
                schema: "Core",
                table: "Conversation",
                columns: new[] { "UserId", "DateDeactivated", "IsFavorite", "DateCreated" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocument_ConversationId_DateDeactivated",
                schema: "Core",
                table: "ConversationDocument",
                columns: new[] { "ConversationId", "DateDeactivated" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocument_UserId",
                schema: "Core",
                table: "ConversationDocument",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationDocumentChunk_ConversationDocumentId_Index",
                schema: "Core",
                table: "ConversationDocumentChunk",
                columns: new[] { "ConversationDocumentId", "Index" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsage_ConversationId_Kind_DateCreated",
                schema: "Core",
                table: "ConversationUsage",
                columns: new[] { "ConversationId", "Kind", "DateCreated" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsage_ModelId_DateCreated",
                schema: "Core",
                table: "ConversationUsage",
                columns: new[] { "ModelId", "DateCreated" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsage_ProviderId",
                schema: "Core",
                table: "ConversationUsage",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsage_UserId_DateCreated",
                schema: "Core",
                table: "ConversationUsage",
                columns: new[] { "UserId", "DateCreated" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageMcpServer_ConversationUsageId_McpServerId",
                schema: "Core",
                table: "ConversationUsageMcpServer",
                columns: new[] { "ConversationUsageId", "McpServerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageMcpServer_McpServerId",
                schema: "Core",
                table: "ConversationUsageMcpServer",
                column: "McpServerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageToolCall_ConversationUsageId_Sequence",
                schema: "Core",
                table: "ConversationUsageToolCall",
                columns: new[] { "ConversationUsageId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageToolCall_Kind_DateCreated",
                schema: "Core",
                table: "ConversationUsageToolCall",
                columns: new[] { "Kind", "DateCreated" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageToolCall_McpServerId_DateCreated",
                schema: "Core",
                table: "ConversationUsageToolCall",
                columns: new[] { "McpServerId", "DateCreated" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageToolCall_ModelId",
                schema: "Core",
                table: "ConversationUsageToolCall",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsageToolCall_ParentId",
                schema: "Core",
                table: "ConversationUsageToolCall",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_McpServer_CreatedById",
                schema: "Core.Ref",
                table: "McpServer",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_McpServer_ModifiedById",
                schema: "Core.Ref",
                table: "McpServer",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_McpServer_Name",
                schema: "Core.Ref",
                table: "McpServer",
                column: "Name",
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Model_CreatedById",
                schema: "Core.Ref",
                table: "Model",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Model_ModifiedById",
                schema: "Core.Ref",
                table: "Model",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Model_ProviderId",
                schema: "Core.Ref",
                table: "Model",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Permission_CreatedById",
                schema: "Core",
                table: "Permission",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Permission_McpServerId",
                schema: "Core",
                table: "Permission",
                column: "McpServerId");

            migrationBuilder.CreateIndex(
                name: "IX_Permission_ModifiedById",
                schema: "Core",
                table: "Permission",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Permission_Name",
                schema: "Core",
                table: "Permission",
                column: "Name",
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Project_UserId_DateDeactivated",
                schema: "Core",
                table: "Project",
                columns: new[] { "UserId", "DateDeactivated" });

            migrationBuilder.CreateIndex(
                name: "IX_Project_UserId_Name",
                schema: "Core",
                table: "Project",
                columns: new[] { "UserId", "Name" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocument_ProjectId_DateDeactivated",
                schema: "Core",
                table: "ProjectDocument",
                columns: new[] { "ProjectId", "DateDeactivated" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocument_UserId",
                schema: "Core",
                table: "ProjectDocument",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentChunk_ProjectDocumentId_Index",
                schema: "Core",
                table: "ProjectDocumentChunk",
                columns: new[] { "ProjectDocumentId", "Index" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermission_CreatedById",
                schema: "Core",
                table: "UserPermission",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermission_ModifiedById",
                schema: "Core",
                table: "UserPermission",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermission_PermissionId",
                schema: "Core",
                table: "UserPermission",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermission_UserId_PermissionId",
                schema: "Core",
                table: "UserPermission",
                columns: new[] { "UserId", "PermissionId" },
                unique: true,
                filter: "[DateDeactivated] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationDocumentChunk",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ConversationUsageMcpServer",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ConversationUsageToolCall",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ProjectDocumentChunk",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "UserPermission",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ConversationDocument",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ConversationUsage",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "ProjectDocument",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "Permission",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "Conversation",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "McpServer",
                schema: "Core.Ref");

            migrationBuilder.DropTable(
                name: "Model",
                schema: "Core.Ref");

            migrationBuilder.DropTable(
                name: "Project",
                schema: "Core");

            migrationBuilder.DropTable(
                name: "Provider",
                schema: "Core.Ref");

            migrationBuilder.DropTable(
                name: "User",
                schema: "Core");
        }
    }
}
