using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramImageDerivative : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InstagramImageGeneratedAt",
                table: "Media",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstagramImageHeight",
                table: "Media",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstagramImageMimeType",
                table: "Media",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InstagramImageSizeBytes",
                table: "Media",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstagramImageStorageKey",
                table: "Media",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstagramImageWidth",
                table: "Media",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InstagramImageGeneratedAt",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "InstagramImageHeight",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "InstagramImageMimeType",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "InstagramImageSizeBytes",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "InstagramImageStorageKey",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "InstagramImageWidth",
                table: "Media");
        }
    }
}
