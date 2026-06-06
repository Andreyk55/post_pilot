using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <summary>
    /// Makes provider-account ownership PERMANENT across workspaces.
    ///
    /// Product rule: a provider account identity (Provider + ProviderAccountId)
    /// belongs forever to the FIRST workspace that connected it. Disconnecting there
    /// must NOT release the identity, so the same external account can never be
    /// connected to another workspace later.
    ///
    /// Implementation: replace the previous ACTIVE-ONLY partial unique index
    ///   unique(Provider, ProviderAccountId) WHERE IsConnected = true AND ProviderAccountId IS NOT NULL
    /// with a PERMANENT one that ignores IsConnected
    ///   unique(Provider, ProviderAccountId) WHERE ProviderAccountId IS NOT NULL
    /// so even a disconnected row reserves the identity.
    /// </summary>
    public partial class MakeProviderAccountOwnershipPermanent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_Provider_ProviderAccountId",
                table: "MetaConnections");

            // ── Defensive cleanup: collapse pre-existing duplicates ────────────────
            // The new permanent index spans connected AND disconnected rows, so any
            // (Provider, ProviderAccountId) that historically appeared in more than one
            // row — e.g. the bug state where ws1 disconnected and ws2 then connected the
            // same account, or multiple disconnected reconnect cycles — would now collide.
            //
            // Keep the EARLIEST connector as the permanent owner (the workspace that first
            // claimed the identity) and hard-delete the rest. We delete (not soft-disconnect)
            // because a surviving duplicate disconnected row would still violate the unique
            // index. Per the change spec, legacy duplicate disconnected rows do not need to
            // be preserved. Child Pages/IGs FK to MetaConnectionId with ON DELETE SET NULL,
            // so deleting a parent row leaves its (already historical) assets orphaned but
            // intact — it never cascades into Posts.
            migrationBuilder.Sql(@"
DELETE FROM ""MetaConnections"" AS m
WHERE m.""ProviderAccountId"" IS NOT NULL
  AND m.""Id"" NOT IN (
      SELECT DISTINCT ON (""Provider"", ""ProviderAccountId"") ""Id""
      FROM ""MetaConnections""
      WHERE ""ProviderAccountId"" IS NOT NULL
      ORDER BY ""Provider"", ""ProviderAccountId"", ""ConnectedAt"" ASC
  );
");

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_Provider_ProviderAccountId",
                table: "MetaConnections",
                columns: new[] { "Provider", "ProviderAccountId" },
                unique: true,
                filter: "\"ProviderAccountId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_Provider_ProviderAccountId",
                table: "MetaConnections");

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_Provider_ProviderAccountId",
                table: "MetaConnections",
                columns: new[] { "Provider", "ProviderAccountId" },
                unique: true,
                filter: "\"IsConnected\" = true AND \"ProviderAccountId\" IS NOT NULL");
        }
    }
}
