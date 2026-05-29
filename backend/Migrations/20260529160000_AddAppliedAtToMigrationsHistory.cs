using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <summary>
    /// Adds an <c>AppliedAt</c> timestamp to <c>__EFMigrationsHistory</c> so we can see
    /// when each migration was actually applied to the database. EF Core normally
    /// stores only MigrationId + ProductVersion — there's no apply-time column out
    /// of the box, which made it hard to confirm whether a prod deploy had picked
    /// up the latest migration.
    ///
    /// The column defaults to <c>NOW()</c>, so EF Core's standard insert
    /// (MigrationId, ProductVersion only) gets the timestamp stamped automatically
    /// without any change to the runner.
    ///
    /// Existing rows are backfilled with <c>NOW()</c> — these are NOT real historical
    /// apply dates, just the moment this migration runs. We can't recover the true
    /// dates after the fact.
    /// </summary>
    public partial class AddAppliedAtToMigrationsHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add nullable first so the backfill can populate it before we set NOT NULL.
            migrationBuilder.Sql(@"
ALTER TABLE ""__EFMigrationsHistory""
    ADD COLUMN IF NOT EXISTS ""AppliedAt"" timestamp with time zone;

UPDATE ""__EFMigrationsHistory""
SET ""AppliedAt"" = NOW()
WHERE ""AppliedAt"" IS NULL;

ALTER TABLE ""__EFMigrationsHistory""
    ALTER COLUMN ""AppliedAt"" SET NOT NULL,
    ALTER COLUMN ""AppliedAt"" SET DEFAULT NOW();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""__EFMigrationsHistory""
    DROP COLUMN IF EXISTS ""AppliedAt"";
");
        }
    }
}
