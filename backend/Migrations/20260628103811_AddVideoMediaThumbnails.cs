using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoMediaThumbnails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ThumbnailCreatedAtUtc",
                table: "Media",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThumbnailHeight",
                table: "Media",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailMimeType",
                table: "Media",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ThumbnailSizeBytes",
                table: "Media",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailStorageKey",
                table: "Media",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThumbnailWidth",
                table: "Media",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThumbnailCreatedAtUtc",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ThumbnailHeight",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ThumbnailMimeType",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ThumbnailSizeBytes",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ThumbnailStorageKey",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ThumbnailWidth",
                table: "Media");
        }
    }
}
