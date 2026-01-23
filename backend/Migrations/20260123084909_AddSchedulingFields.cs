using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalPostId",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetries",
                table: "Posts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "Posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "Posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "Posts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleArn",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetPageId",
                table: "Posts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Status_NextRetryAt",
                table: "Posts",
                columns: new[] { "Status", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Status_ScheduledAt",
                table: "Posts",
                columns: new[] { "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_TargetPageId",
                table: "Posts",
                column: "TargetPageId");

            migrationBuilder.AddForeignKey(
                name: "FK_Posts_ConnectedPages_TargetPageId",
                table: "Posts",
                column: "TargetPageId",
                principalTable: "ConnectedPages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Posts_ConnectedPages_TargetPageId",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_Status_NextRetryAt",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_Status_ScheduledAt",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_TargetPageId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ExternalPostId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "MaxRetries",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ScheduleArn",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "TargetPageId",
                table: "Posts");
        }
    }
}
