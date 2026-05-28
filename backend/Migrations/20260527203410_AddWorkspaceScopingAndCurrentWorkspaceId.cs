using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceScopingAndCurrentWorkspaceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_UserId",
                table: "MetaConnections");

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "Posts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "PostMediaItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "MetaOAuthStates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "MetaConnections",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "Media",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "ConnectedPages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "ConnectedInstagramAccounts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentWorkspaceId",
                table: "AppUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "AiVoiceProfiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Posts_WorkspaceId",
                table: "Posts",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PostMediaItems_WorkspaceId",
                table: "PostMediaItems",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_WorkspaceId",
                table: "MetaConnections",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_WorkspaceId_UserId",
                table: "MetaConnections",
                columns: new[] { "WorkspaceId", "UserId" },
                unique: true,
                filter: "\"IsConnected\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_Media_WorkspaceId",
                table: "Media",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedPages_WorkspaceId",
                table: "ConnectedPages",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedInstagramAccounts_WorkspaceId",
                table: "ConnectedInstagramAccounts",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_AiVoiceProfiles_WorkspaceId",
                table: "AiVoiceProfiles",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_WorkspaceId",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_PostMediaItems_WorkspaceId",
                table: "PostMediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_WorkspaceId",
                table: "MetaConnections");

            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_WorkspaceId_UserId",
                table: "MetaConnections");

            migrationBuilder.DropIndex(
                name: "IX_Media_WorkspaceId",
                table: "Media");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedPages_WorkspaceId",
                table: "ConnectedPages");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedInstagramAccounts_WorkspaceId",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.DropIndex(
                name: "IX_AiVoiceProfiles_WorkspaceId",
                table: "AiVoiceProfiles");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "PostMediaItems");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "MetaOAuthStates");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "MetaConnections");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "ConnectedPages");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.DropColumn(
                name: "CurrentWorkspaceId",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "AiVoiceProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_UserId",
                table: "MetaConnections",
                column: "UserId",
                unique: true,
                filter: "\"IsConnected\" = true");
        }
    }
}
