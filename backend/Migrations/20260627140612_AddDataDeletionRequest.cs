using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDataDeletionRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataDeletionRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfirmationCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    ProviderAccountId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Warning = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataDeletionRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataDeletionRequests_ConfirmationCode",
                table: "DataDeletionRequests",
                column: "ConfirmationCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataDeletionRequests_Provider_ProviderAccountId",
                table: "DataDeletionRequests",
                columns: new[] { "Provider", "ProviderAccountId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataDeletionRequests");
        }
    }
}
