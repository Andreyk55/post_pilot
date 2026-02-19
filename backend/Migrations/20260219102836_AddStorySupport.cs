using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStorySupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FacebookStoryMediaId",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostType",
                table: "Posts",
                type: "text",
                nullable: false,
                defaultValue: "Feed");

            migrationBuilder.AddColumn<string>(
                name: "ProfileUrl",
                table: "Posts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FacebookStoryMediaId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "PostType",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ProfileUrl",
                table: "Posts");
        }
    }
}
