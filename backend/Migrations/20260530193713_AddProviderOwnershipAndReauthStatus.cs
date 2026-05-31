using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <summary>
    /// Generic cross-workspace provider OWNERSHIP + reauth status.
    ///
    /// Adds a <c>Status</c> column (0 = Active, 1 = ReauthRequired) to MetaConnections,
    /// ConnectedPages and ConnectedInstagramAccounts. Status refines the existing
    /// <c>IsConnected</c> ownership flag: IsConnected = true means the workspace OWNS
    /// the account/asset (whether Active or ReauthRequired); only a real disconnect
    /// (IsConnected = false) releases ownership.
    ///
    /// Replaces the previous PER-WORKSPACE uniqueness on pages/IG accounts with
    /// CROSS-WORKSPACE partial unique indexes so a provider account/page/IG account
    /// can be owned by only ONE workspace at a time:
    ///   - MetaConnections:  unique (Provider, ProviderAccountId) WHERE IsConnected
    ///   - ConnectedPages:   unique (PageId)                       WHERE IsConnected
    ///   - ConnectedIGs:     unique (IgBusinessId)                 WHERE IsConnected
    ///
    /// Transitional safety: the previous schema explicitly ALLOWED the same external
    /// page/IG in two workspaces ("agency use case"). Those rows would now collide on
    /// the new unique indexes, so before creating them we soft-disconnect the older
    /// duplicate owner(s), keeping the most-recently-connected workspace as owner.
    /// </summary>
    public partial class AddProviderOwnershipAndReauthStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedPages_WorkspaceId_PageId",
                table: "ConnectedPages");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedInstagramAccounts_WorkspaceId_IgBusinessId",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "MetaConnections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "ConnectedPages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "ConnectedInstagramAccounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // ── Defensive cleanup: resolve pre-existing cross-workspace duplicates ──
            // so the new partial-unique indexes can be created. Keep the most-recently
            // connected owner; soft-disconnect the rest. No-op on any DB that never had
            // the same external id owned by two workspaces.
            //
            // MetaConnections: dedupe by (Provider, ProviderAccountId) among IsConnected
            // rows with a non-null ProviderAccountId.
            migrationBuilder.Sql(@"
UPDATE ""MetaConnections"" AS m
SET ""IsConnected"" = false,
    ""DisconnectedAt"" = COALESCE(""DisconnectedAt"", NOW()),
    ""Status"" = 0
WHERE m.""IsConnected"" = true
  AND m.""ProviderAccountId"" IS NOT NULL
  AND m.""Id"" NOT IN (
      SELECT DISTINCT ON (""Provider"", ""ProviderAccountId"") ""Id""
      FROM ""MetaConnections""
      WHERE ""IsConnected"" = true AND ""ProviderAccountId"" IS NOT NULL
      ORDER BY ""Provider"", ""ProviderAccountId"", ""ConnectedAt"" DESC
  );
");

            // ConnectedPages: dedupe by PageId among IsConnected rows. Keep the row
            // whose parent connection was most recently connected (fallback: CreatedAt).
            migrationBuilder.Sql(@"
UPDATE ""ConnectedPages"" AS p
SET ""IsConnected"" = false,
    ""DisconnectedAt"" = COALESCE(""DisconnectedAt"", NOW()),
    ""Status"" = 0
WHERE p.""IsConnected"" = true
  AND p.""Id"" NOT IN (
      SELECT DISTINCT ON (""PageId"") ""Id""
      FROM ""ConnectedPages""
      WHERE ""IsConnected"" = true
      ORDER BY ""PageId"", ""CreatedAt"" DESC
  );
");

            // ConnectedInstagramAccounts: dedupe by IgBusinessId among IsConnected rows.
            migrationBuilder.Sql(@"
UPDATE ""ConnectedInstagramAccounts"" AS i
SET ""IsConnected"" = false,
    ""DisconnectedAt"" = COALESCE(""DisconnectedAt"", NOW()),
    ""Status"" = 0
WHERE i.""IsConnected"" = true
  AND i.""Id"" NOT IN (
      SELECT DISTINCT ON (""IgBusinessId"") ""Id""
      FROM ""ConnectedInstagramAccounts""
      WHERE ""IsConnected"" = true
      ORDER BY ""IgBusinessId"", ""CreatedAt"" DESC
  );
");

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_Provider_ProviderAccountId",
                table: "MetaConnections",
                columns: new[] { "Provider", "ProviderAccountId" },
                unique: true,
                filter: "\"IsConnected\" = true AND \"ProviderAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedPages_PageId_Owned",
                table: "ConnectedPages",
                column: "PageId",
                unique: true,
                filter: "\"IsConnected\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedInstagramAccounts_IgBusinessId_Owned",
                table: "ConnectedInstagramAccounts",
                column: "IgBusinessId",
                unique: true,
                filter: "\"IsConnected\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_Provider_ProviderAccountId",
                table: "MetaConnections");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedPages_PageId_Owned",
                table: "ConnectedPages");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedInstagramAccounts_IgBusinessId_Owned",
                table: "ConnectedInstagramAccounts");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "MetaConnections");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ConnectedPages");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ConnectedInstagramAccounts");

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
        }
    }
}
