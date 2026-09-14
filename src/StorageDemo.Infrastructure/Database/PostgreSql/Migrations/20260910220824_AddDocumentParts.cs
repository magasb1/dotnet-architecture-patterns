using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StorageDemo.Infrastructure.Database.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An empty JSON array, not an empty string: jsonb refuses '' and every existing row
            // needs a value it can actually hold. A document written in one go has no pieces.
            migrationBuilder.AddColumn<string>(
                name: "Parts",
                table: "documents",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Parts",
                table: "documents");
        }
    }
}
