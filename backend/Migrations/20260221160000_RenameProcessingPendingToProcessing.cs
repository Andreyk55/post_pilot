using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PostPilot.Api.Migrations
{
    /// <inheritdoc />
    public partial class RenameProcessingPendingToProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data patch: rename any legacy "ProcessingPending" status values to "Processing".
            // The PostStatus enum is stored as a string; this ensures existing rows are compatible.
            migrationBuilder.Sql(
                "UPDATE \"Posts\" SET \"Status\" = 'Processing' WHERE \"Status\" = 'ProcessingPending';");

            // Backfill: rows that were stored as RetryPending but are actually waiting for
            // Meta media processing (ProcessingPollCount > 0, RetryCount = 0).
            migrationBuilder.Sql(
                "UPDATE \"Posts\" SET \"Status\" = 'Processing' WHERE \"Status\" = 'RetryPending' AND \"ProcessingPollCount\" > 0 AND \"RetryCount\" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"Posts\" SET \"Status\" = 'ProcessingPending' WHERE \"Status\" = 'Processing';");
        }
    }
}
