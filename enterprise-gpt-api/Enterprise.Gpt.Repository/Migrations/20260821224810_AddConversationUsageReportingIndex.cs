using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationUsageReportingIndex : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// This runs inside <c>Database.Migrate()</c> at application startup, and a covering index
        /// over <c>Core.ConversationUsage</c> is an <b>offline</b> build by default: it takes a
        /// schema-modification lock and startup blocks until it finishes. That table gains a row
        /// per model call and nothing prunes it, so on a deployment with real history this is a
        /// deploy-time pause proportional to the row count rather than an ordinary migration.
        /// </para>
        /// <para>
        /// An operator who cannot take that pause should create the index ahead of the deployment
        /// with <c>WITH (ONLINE = ON)</c> and then insert this migration's row into
        /// <c>__EFMigrationsHistory</c>, which makes the startup run a no-op. It is not written
        /// that way here because <c>ONLINE = ON</c> is unavailable on some editions and would fail
        /// the deployment outright rather than slow it down.
        /// </para>
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ConversationUsage_DateCreated",
                schema: "Core",
                table: "ConversationUsage",
                column: "DateCreated")
                .Annotation("SqlServer:Include", new[] { "UserId", "ConversationId", "ModelId", "ProviderId", "Kind", "Status", "InputTokens", "OutputTokens", "ToolInputTokens", "ToolOutputTokens", "ContextTokens", "InputPricePerMillionTokens", "OutputPricePerMillionTokens" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationUsage_DateCreated",
                schema: "Core",
                table: "ConversationUsage");
        }
    }
}
