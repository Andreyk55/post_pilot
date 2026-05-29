using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <summary>
    /// Generic provider lifecycle landing migration.
    ///
    /// Adds the stable provider-account identity columns to <c>MetaConnections</c>
    /// so the lifecycle (one active connection per workspace per provider; reconnect
    /// same account ⇒ resurface history) can be implemented without a Meta-only schema.
    ///
    /// Also stamps cancellation metadata onto <c>Posts</c> so canceled rows know
    /// WHY they were canceled and which provider account they belonged to —
    /// the latter is required so reconnecting the same provider account can
    /// resurface its canceled history without restoring the cancellations.
    ///
    /// Transitional safety:
    ///  - <c>ProviderAccountId</c> is nullable. Existing dev rows (no real prod data
    ///    yet — workspace scoping just landed) will have NULL until the next connect.
    ///    The <c>Provider</c> column defaults to 0 (<c>ProviderType.Meta</c>), which
    ///    is correct for every existing row since Meta is the only provider today.
    ///  - The legacy unique index <c>IX_MetaConnections_WorkspaceId_UserId</c> is
    ///    dropped and replaced with <c>IX_MetaConnections_WorkspaceId_Provider</c>.
    ///    Any pre-existing duplicate active rows (same workspace, multiple users)
    ///    would have collided on the new index — see the defensive SQL below.
    /// </summary>
    public partial class AddProviderIdentityAndCancellationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── MetaConnections ──────────────────────────────────────────────
            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "MetaConnections",
                type: "integer",
                nullable: false,
                defaultValue: 0); // 0 = ProviderType.Meta

            migrationBuilder.AddColumn<string>(
                name: "ProviderAccountId",
                table: "MetaConnections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderAccountName",
                table: "MetaConnections",
                type: "text",
                nullable: true);

            // Defensive cleanup: if any workspace has two ACTIVE MetaConnections
            // (legacy multi-user-per-workspace data), keep only the most recent
            // one as active and soft-disconnect the rest. The new unique index
            // would otherwise refuse to create.
            //
            // This is a no-op on a fresh dev DB and on any DB that never had
            // multiple active Meta connections per workspace.
            migrationBuilder.Sql(@"
UPDATE ""MetaConnections"" AS m
SET ""IsConnected"" = false,
    ""DisconnectedAt"" = COALESCE(""DisconnectedAt"", NOW())
WHERE m.""IsConnected"" = true
  AND m.""Id"" NOT IN (
      SELECT DISTINCT ON (""WorkspaceId"", ""Provider"") ""Id""
      FROM ""MetaConnections""
      WHERE ""IsConnected"" = true
      ORDER BY ""WorkspaceId"", ""Provider"", ""ConnectedAt"" DESC
  );
");

            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_WorkspaceId_UserId",
                table: "MetaConnections");

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_WorkspaceId_Provider",
                table: "MetaConnections",
                columns: new[] { "WorkspaceId", "Provider" },
                unique: true,
                filter: "\"IsConnected\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_WorkspaceId_Provider_ProviderAccountId",
                table: "MetaConnections",
                columns: new[] { "WorkspaceId", "Provider", "ProviderAccountId" });

            // ── Posts (cancellation metadata) ────────────────────────────────
            migrationBuilder.AddColumn<int>(
                name: "CancellationReason",
                table: "Posts",
                type: "integer",
                nullable: false,
                defaultValue: 0); // 0 = CancellationReason.None

            migrationBuilder.AddColumn<int>(
                name: "CanceledBecauseProvider",
                table: "Posts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CanceledBecauseProviderAccountId",
                table: "Posts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanceledBecauseProviderAccountId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "CanceledBecauseProvider",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_WorkspaceId_Provider_ProviderAccountId",
                table: "MetaConnections");

            migrationBuilder.DropIndex(
                name: "IX_MetaConnections_WorkspaceId_Provider",
                table: "MetaConnections");

            // Best-effort: restore the legacy unique index. If duplicate active
            // (WorkspaceId, UserId) rows exist after the up-migration ran, this
            // will fail and a manual cleanup is required.
            migrationBuilder.CreateIndex(
                name: "IX_MetaConnections_WorkspaceId_UserId",
                table: "MetaConnections",
                columns: new[] { "WorkspaceId", "UserId" },
                unique: true,
                filter: "\"IsConnected\" = true");

            migrationBuilder.DropColumn(
                name: "ProviderAccountName",
                table: "MetaConnections");

            migrationBuilder.DropColumn(
                name: "ProviderAccountId",
                table: "MetaConnections");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "MetaConnections");
        }
    }
}
