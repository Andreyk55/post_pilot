using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetaConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccessToken = table.Column<string>(type: "text", nullable: false),
                    TokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetaOAuthStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    TempAccessToken = table.Column<string>(type: "text", nullable: true),
                    TokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaOAuthStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedInstagramAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IgBusinessId = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    ProfilePictureUrl = table.Column<string>(type: "text", nullable: true),
                    PageId = table.Column<string>(type: "text", nullable: false),
                    PageName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedInstagramAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedInstagramAccounts_MetaConnections_MetaConnectionId",
                        column: x => x.MetaConnectionId,
                        principalTable: "MetaConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: true),
                    PictureUrl = table.Column<string>(type: "text", nullable: true),
                    AccessToken = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedPages_MetaConnections_MetaConnectionId",
                        column: x => x.MetaConnectionId,
                        principalTable: "MetaConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedInstagramAccounts_MetaConnectionId_IgBusinessId",
                table: "ConnectedInstagramAccounts",
                columns: new[] { "MetaConnectionId", "IgBusinessId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedPages_MetaConnectionId_PageId",
                table: "ConnectedPages",
                columns: new[] { "MetaConnectionId", "PageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_UserId",
                table: "MetaConnections",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaOAuthStates_State",
                table: "MetaOAuthStates",
                column: "State",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectedInstagramAccounts");

            migrationBuilder.DropTable(
                name: "ConnectedPages");

            migrationBuilder.DropTable(
                name: "MetaOAuthStates");

            migrationBuilder.DropTable(
                name: "MetaConnections");
        }
    }
}
