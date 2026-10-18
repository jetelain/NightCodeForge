using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionBatchId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BatchId",
                table: "Questions",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_BatchId",
                table: "Questions",
                column: "BatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Questions_BatchId",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "Questions");
        }
    }
}
