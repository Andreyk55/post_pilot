using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportEmailNotificationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmailNotificationError",
                table: "SupportContactRequests",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailNotificationSentAt",
                table: "SupportContactRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmailNotificationStatus",
                table: "SupportContactRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailNotificationError",
                table: "SupportContactRequests");

            migrationBuilder.DropColumn(
                name: "EmailNotificationSentAt",
                table: "SupportContactRequests");

            migrationBuilder.DropColumn(
                name: "EmailNotificationStatus",
                table: "SupportContactRequests");
        }
    }
}
