using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceGlobalSkillsSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisabledGlobalSkillsJson",
                table: "Workspaces",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GlobalSkillsInjectionMode",
                table: "Workspaces",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisabledGlobalSkillsJson",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "GlobalSkillsInjectionMode",
                table: "Workspaces");
        }
    }
}
