using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StorageDemo.Infrastructure.Database.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddThumbnailAndMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "documents",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailKey",
                table: "documents",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ThumbnailKey",
                table: "documents");
        }
    }
}
