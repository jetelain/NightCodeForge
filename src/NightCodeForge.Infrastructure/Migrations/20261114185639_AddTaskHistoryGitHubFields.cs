using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskHistoryGitHubFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalReferenceId",
                table: "TaskHistories",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "TaskHistories",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PullRequestNumber",
                table: "TaskHistories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestUrl",
                table: "TaskHistories",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalReferenceId",
                table: "TaskHistories");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "TaskHistories");

            migrationBuilder.DropColumn(
                name: "PullRequestNumber",
                table: "TaskHistories");

            migrationBuilder.DropColumn(
                name: "PullRequestUrl",
                table: "TaskHistories");
        }
    }
}
