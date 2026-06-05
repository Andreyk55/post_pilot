using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <summary>
    /// Make the stored credential columns nullable so disconnect can CLEAR them
    /// while keeping the provider identity row:
    ///   - MetaConnections.AccessToken (user/connection token)
    ///   - ConnectedPages.AccessToken  (page token — the asset-level secret that
    ///     can independently publish; IG accounts publish via their linked page).
    ///
    /// Identity columns (WorkspaceId, Provider, ProviderAccountId, ProviderAccountName,
    /// Status, DisconnectedAt) are untouched. A reconnect repopulates the tokens.
    /// </summary>
    public partial class MakeConnectionAndPageTokensNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "AccessToken",
                table: "MetaConnections",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "AccessToken",
                table: "ConnectedPages",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "AccessToken",
                table: "MetaConnections",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AccessToken",
                table: "ConnectedPages",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
