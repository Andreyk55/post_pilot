using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceForeignKeysAndUniques : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defensive cleanup before adding workspace FKs.
            //
            // The earlier AddWorkspaceScopingAndCurrentWorkspaceId migration backfilled
            // every existing row's WorkspaceId to Guid.Empty. On a fresh DB this is a
            // no-op (no rows existed yet). On any DB that carried pre-workspaces data
            // those rows would now violate the new FK to Workspaces.
            //
            // Strategy: delete any row whose WorkspaceId points at a workspace that
            // doesn't exist. They are invisible to the workspace-scoped controllers
            // already, so deleting them only removes inaccessible data.
            //
            // BEFORE running this migration on a DB with real data, READ:
            //   docs/workspace-migration-safety.md
            //   scripts/check-workspace-orphans.sql
            // The doc explains how to backfill these rows into a real workspace
            // instead of deleting them, and how to back up first.
            //
            // Order matters: child tables first, then parents.
            migrationBuilder.Sql(@"
DELETE FROM ""PostMediaItems"" WHERE ""WorkspaceId"" NOT IN (SELECT ""Id"" FROM ""Workspaces"");
DELETE FROM ""Posts""           WHERE ""WorkspaceId"" NOT IN (SELECT ""Id"" FROM ""Workspaces"");
DELETE FROM ""Media""           WHERE ""WorkspaceId"" NOT IN (SELECT ""Id"" FROM ""Workspaces"");
DELETE FROM ""AiVoiceProfiles"" WHERE ""WorkspaceId"" NOT IN (SELECT ""Id"" FROM ""Workspaces"");
DELETE FROM ""MetaOAuthStates"" WHERE ""WorkspaceId"" NOT IN (SELECT ""Id"" FROM ""Workspaces"");
DELETE FROM ""ConnectedPages""             WHERE ""WorkspaceId"" NOT IN (SELECT ""Id"" FROM ""Workspaces"");
DELETE FROM ""ConnectedInstagramAccounts"" WHERE ""WorkspaceId"" NOT IN (SELECT ""Id"" FROM ""Workspaces"");
DELETE FROM ""MetaConnections""            WHERE ""WorkspaceId"" NOT IN (SELECT ""Id"" FROM ""Workspaces"");

DELETE FROM ""WorkspaceMembers"" WHERE ""WorkspaceId"" NOT IN (SELECT ""Id"" FROM ""Workspaces"");
DELETE FROM ""WorkspaceMembers"" WHERE ""UserId""      NOT IN (SELECT ""Id"" FROM ""AppUsers"");
DELETE FROM ""Workspaces""       WHERE ""OwnerUserId"" NOT IN (SELECT ""Id"" FROM ""AppUsers"");
");

            migrationBuilder.CreateIndex(
                name: "IX_MetaOAuthStates_WorkspaceId",
                table: "MetaOAuthStates",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedPages_WorkspaceId_PageId",
                table: "ConnectedPages",
                columns: new[] { "WorkspaceId", "PageId" },
                unique: true,
                filter: "\"IsConnected\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedInstagramAccounts_WorkspaceId_IgBusinessId",
                table: "ConnectedInstagramAccounts",
                columns: new[] { "WorkspaceId", "IgBusinessId" },
                unique: true,
                filter: "\"IsConnected\" = true");

            migrationBuilder.AddForeignKey(
                name: "FK_AiVoiceProfiles_Workspaces_WorkspaceId",
                table: "AiVoiceProfiles",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedInstagramAccounts_Workspaces_WorkspaceId",
                table: "ConnectedInstagramAccounts",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedPages_Workspaces_WorkspaceId",
                table: "ConnectedPages",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Media_Workspaces_WorkspaceId",
                table: "Media",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaConnections_Workspaces_WorkspaceId",
                table: "MetaConnections",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaOAuthStates_Workspaces_WorkspaceId",
                table: "MetaOAuthStates",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PostMediaItems_Workspaces_WorkspaceId",
                table: "PostMediaItems",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Posts_Workspaces_WorkspaceId",
                table: "Posts",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkspaceMembers_AppUsers_UserId",
                table: "WorkspaceMembers",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkspaceMembers_Workspaces_WorkspaceId",
                table: "WorkspaceMembers",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Workspaces_AppUsers_OwnerUserId",
                table: "Workspaces",
                column: "OwnerUserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AiVoiceProfiles_Workspaces_WorkspaceId",
                table: "AiVoiceProfiles");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedInstagramAccounts_Workspaces_WorkspaceId",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedPages_Workspaces_WorkspaceId",
                table: "ConnectedPages");

            migrationBuilder.DropForeignKey(
                name: "FK_Media_Workspaces_WorkspaceId",
                table: "Media");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaConnections_Workspaces_WorkspaceId",
                table: "MetaConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaOAuthStates_Workspaces_WorkspaceId",
                table: "MetaOAuthStates");

            migrationBuilder.DropForeignKey(
                name: "FK_PostMediaItems_Workspaces_WorkspaceId",
                table: "PostMediaItems");

            migrationBuilder.DropForeignKey(
                name: "FK_Posts_Workspaces_WorkspaceId",
                table: "Posts");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkspaceMembers_AppUsers_UserId",
                table: "WorkspaceMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkspaceMembers_Workspaces_WorkspaceId",
                table: "WorkspaceMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_Workspaces_AppUsers_OwnerUserId",
                table: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_MetaOAuthStates_WorkspaceId",
                table: "MetaOAuthStates");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedPages_WorkspaceId_PageId",
                table: "ConnectedPages");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedInstagramAccounts_WorkspaceId_IgBusinessId",
                table: "ConnectedInstagramAccounts");
        }
    }
}
