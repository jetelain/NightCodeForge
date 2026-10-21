using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPortalScopingAndExecutionFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasExecutionRun",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxExecutedStageOrder",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkspaceId_HasExecutionRun",
                table: "Tasks",
                columns: new[] { "WorkspaceId", "HasExecutionRun" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkspaceId_Origin_Status",
                table: "Tasks",
                columns: new[] { "WorkspaceId", "Origin", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_WorkspaceId_HasExecutionRun",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_WorkspaceId_Origin_Status",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "HasExecutionRun",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "MaxExecutedStageOrder",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Tasks");
        }
    }
}
