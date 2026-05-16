using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDisconnect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedInstagramAccounts_MetaConnections_MetaConnectionId",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedPages_MetaConnections_MetaConnectionId",
                table: "ConnectedPages");

            migrationBuilder.DropForeignKey(
                name: "FK_Posts_ConnectedInstagramAccounts_TargetInstagramAccountId",
                table: "Posts");

            migrationBuilder.DropForeignKey(
                name: "FK_Posts_ConnectedPages_TargetPageId",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_UserId",
                table: "MetaConnections");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedPages_MetaConnectionId_PageId",
                table: "ConnectedPages");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedInstagramAccounts_MetaConnectionId_IgBusinessId",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.AddColumn<DateTime>(
                name: "DisconnectedAt",
                table: "MetaConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsConnected",
                table: "MetaConnections",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MetaConnectionId",
                table: "ConnectedPages",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTime>(
                name: "DisconnectedAt",
                table: "ConnectedPages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsConnected",
                table: "ConnectedPages",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MetaConnectionId",
                table: "ConnectedInstagramAccounts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTime>(
                name: "DisconnectedAt",
                table: "ConnectedInstagramAccounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsConnected",
                table: "ConnectedInstagramAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_UserId",
                table: "MetaConnections",
                column: "UserId",
                unique: true,
                filter: "\"IsConnected\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedPages_MetaConnectionId_PageId",
                table: "ConnectedPages",
                columns: new[] { "MetaConnectionId", "PageId" },
                unique: true,
                filter: "\"IsConnected\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedInstagramAccounts_MetaConnectionId_IgBusinessId",
                table: "ConnectedInstagramAccounts",
                columns: new[] { "MetaConnectionId", "IgBusinessId" },
                unique: true,
                filter: "\"IsConnected\" = true");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedInstagramAccounts_MetaConnections_MetaConnectionId",
                table: "ConnectedInstagramAccounts",
                column: "MetaConnectionId",
                principalTable: "MetaConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedPages_MetaConnections_MetaConnectionId",
                table: "ConnectedPages",
                column: "MetaConnectionId",
                principalTable: "MetaConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Posts_ConnectedInstagramAccounts_TargetInstagramAccountId",
                table: "Posts",
                column: "TargetInstagramAccountId",
                principalTable: "ConnectedInstagramAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Posts_ConnectedPages_TargetPageId",
                table: "Posts",
                column: "TargetPageId",
                principalTable: "ConnectedPages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedInstagramAccounts_MetaConnections_MetaConnectionId",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedPages_MetaConnections_MetaConnectionId",
                table: "ConnectedPages");

            migrationBuilder.DropForeignKey(
                name: "FK_Posts_ConnectedInstagramAccounts_TargetInstagramAccountId",
                table: "Posts");

            migrationBuilder.DropForeignKey(
                name: "FK_Posts_ConnectedPages_TargetPageId",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_UserId",
                table: "MetaConnections");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedPages_MetaConnectionId_PageId",
                table: "ConnectedPages");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedInstagramAccounts_MetaConnectionId_IgBusinessId",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.DropColumn(
                name: "DisconnectedAt",
                table: "MetaConnections");

            migrationBuilder.DropColumn(
                name: "IsConnected",
                table: "MetaConnections");

            migrationBuilder.DropColumn(
                name: "DisconnectedAt",
                table: "ConnectedPages");

            migrationBuilder.DropColumn(
                name: "IsConnected",
                table: "ConnectedPages");

            migrationBuilder.DropColumn(
                name: "DisconnectedAt",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.DropColumn(
                name: "IsConnected",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.AlterColumn<Guid>(
                name: "MetaConnectionId",
                table: "ConnectedPages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MetaConnectionId",
                table: "ConnectedInstagramAccounts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_UserId",
                table: "MetaConnections",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedPages_MetaConnectionId_PageId",
                table: "ConnectedPages",
                columns: new[] { "MetaConnectionId", "PageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedInstagramAccounts_MetaConnectionId_IgBusinessId",
                table: "ConnectedInstagramAccounts",
                columns: new[] { "MetaConnectionId", "IgBusinessId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedInstagramAccounts_MetaConnections_MetaConnectionId",
                table: "ConnectedInstagramAccounts",
                column: "MetaConnectionId",
                principalTable: "MetaConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedPages_MetaConnections_MetaConnectionId",
                table: "ConnectedPages",
                column: "MetaConnectionId",
                principalTable: "MetaConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Posts_ConnectedInstagramAccounts_TargetInstagramAccountId",
                table: "Posts",
                column: "TargetInstagramAccountId",
                principalTable: "ConnectedInstagramAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Posts_ConnectedPages_TargetPageId",
                table: "Posts",
                column: "TargetPageId",
                principalTable: "ConnectedPages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
