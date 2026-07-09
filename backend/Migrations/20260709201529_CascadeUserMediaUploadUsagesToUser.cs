using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class CascadeUserMediaUploadUsagesToUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Account deletion previously left usage rows behind, so orphans may already
            // exist; they would violate the new FK. Purge them before adding the constraint.
            migrationBuilder.Sql(
                """
                DELETE FROM "UserMediaUploadUsages" u
                WHERE NOT EXISTS (SELECT 1 FROM "AppUsers" a WHERE a."Id" = u."UserId");
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_UserMediaUploadUsages_AppUsers_UserId",
                table: "UserMediaUploadUsages",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserMediaUploadUsages_AppUsers_UserId",
                table: "UserMediaUploadUsages");
        }
    }
}
