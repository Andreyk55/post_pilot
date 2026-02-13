using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramPostFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MediaAssets\";");

            migrationBuilder.AddColumn<string>(
                name: "ExternalPostUrl",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetInstagramAccountId",
                table: "Posts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_TargetInstagramAccountId",
                table: "Posts",
                column: "TargetInstagramAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Posts_ConnectedInstagramAccounts_TargetInstagramAccountId",
                table: "Posts",
                column: "TargetInstagramAccountId",
                principalTable: "ConnectedInstagramAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Posts_ConnectedInstagramAccounts_TargetInstagramAccountId",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_TargetInstagramAccountId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ExternalPostUrl",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "TargetInstagramAccountId",
                table: "Posts");

            migrationBuilder.CreateTable(
                name: "MediaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    MediaType = table.Column<string>(type: "text", nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    MimeType = table.Column<string>(type: "text", nullable: false),
                    OriginalFileName = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "text", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidationErrorsJson = table.Column<string>(type: "text", nullable: true),
                    ValidationPlacement = table.Column<string>(type: "text", nullable: true),
                    ValidationPlatform = table.Column<string>(type: "text", nullable: true),
                    ValidationStatus = table.Column<string>(type: "text", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_StorageKey",
                table: "MediaAssets",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_ValidationStatus",
                table: "MediaAssets",
                column: "ValidationStatus");
        }
    }
}
