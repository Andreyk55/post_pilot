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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"Posts\" SET \"Status\" = 'ProcessingPending' WHERE \"Status\" = 'Processing';");
        }
    }
}
