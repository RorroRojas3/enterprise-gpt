using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enterprise.Gpt.Repository.Migrations
{
    /// <summary>
    /// Adds the uploaded/generated discriminator to <c>Core.ConversationDocument</c>.
    /// </summary>
    /// <remarks>
    /// The constant default is the backfill: every row that existed before this ran is an upload, and
    /// the CLR default EF would otherwise scaffold is a value the enum does not define. It lands on
    /// <c>ConversationDocument</c> alone, so <c>ProjectDocument</c> is untouched.
    /// </remarks>
    public partial class AddConversationDocumentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Type",
                schema: "Core",
                table: "ConversationDocument",
                type: "int",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                schema: "Core",
                table: "ConversationDocument");
        }
    }
}
