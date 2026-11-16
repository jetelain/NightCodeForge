using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskDifficultyAndProfileDifficultyOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Difficulty",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Difficulty",
                table: "TaskHistories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DifficultyOverrides",
                table: "AgentProfiles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Difficulty",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Difficulty",
                table: "TaskHistories");

            migrationBuilder.DropColumn(
                name: "DifficultyOverrides",
                table: "AgentProfiles");
        }
    }
}
