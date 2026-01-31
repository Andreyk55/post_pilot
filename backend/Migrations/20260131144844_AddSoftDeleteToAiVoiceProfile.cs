using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDeleteToAiVoiceProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "AiVoiceProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "AiVoiceProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "AiVoiceProfiles");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "AiVoiceProfiles");
        }
    }
}
