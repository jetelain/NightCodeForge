using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentProfileContextWindowTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContextWindowTokens",
                table: "AgentProfiles",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextWindowTokens",
                table: "AgentProfiles");
        }
    }
}
