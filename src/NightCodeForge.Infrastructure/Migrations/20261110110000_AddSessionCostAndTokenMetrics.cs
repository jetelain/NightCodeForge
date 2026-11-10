using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionCostAndTokenMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ActualCloudCost",
                table: "StageExecutionRecords",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CachedInputTokens",
                table: "StageExecutionRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CloudSavings",
                table: "StageExecutionRecords",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Cost",
                table: "StageExecutionRecords",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "StageExecutionRecords",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ElectricityCost",
                table: "StageExecutionRecords",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ActualCloudCost",
                table: "Conversations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CachedInputTokens",
                table: "Conversations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CloudSavings",
                table: "Conversations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Cost",
                table: "Conversations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Conversations",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ElectricityCost",
                table: "Conversations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "Conversations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "Conversations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualCloudCost",
                table: "StageExecutionRecords");

            migrationBuilder.DropColumn(
                name: "CachedInputTokens",
                table: "StageExecutionRecords");

            migrationBuilder.DropColumn(
                name: "CloudSavings",
                table: "StageExecutionRecords");

            migrationBuilder.DropColumn(
                name: "Cost",
                table: "StageExecutionRecords");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "StageExecutionRecords");

            migrationBuilder.DropColumn(
                name: "ElectricityCost",
                table: "StageExecutionRecords");

            migrationBuilder.DropColumn(
                name: "ActualCloudCost",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "CachedInputTokens",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "CloudSavings",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Cost",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ElectricityCost",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "Conversations");
        }
    }
}

