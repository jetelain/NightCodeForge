using Microsoft.EntityFrameworkCore.Migrations;
using NightCodeForge.Core.Domain;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStageQueueTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QueueTier",
                table: "StageDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Data migration: Set canonical QueueTier for pre-existing stages / steps
            // Tier 1 (TaskReview = 1): TaskReview, Triage, Ideation
            migrationBuilder.Sql(
                $"UPDATE StageDefinitions SET QueueTier = {(int)StageQueueTier.TaskReview} WHERE StageId IN ('{StandardStageIds.TaskReview}', '{StandardStageIds.Triage}', '{StandardStageIds.Ideation}');");

            // Tier 2 (Implementation = 2): PreImplementationBaseline, Implementation, PostImplementationGates, Merge
            migrationBuilder.Sql(
                $"UPDATE StageDefinitions SET QueueTier = {(int)StageQueueTier.Implementation} WHERE StageId IN ('{StandardStageIds.PreImplementationBaseline}', '{StandardStageIds.Implementation}', '{StandardStageIds.PostImplementationGates}', '{StandardStageIds.Merge}');");

            // Tier 3 (CodeReview = 3): CodeReviewAndFix, PostReviewVerification
            migrationBuilder.Sql(
                $"UPDATE StageDefinitions SET QueueTier = {(int)StageQueueTier.CodeReview} WHERE StageId IN ('{StandardStageIds.CodeReviewAndFix}', '{StandardStageIds.PostReviewVerification}');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QueueTier",
                table: "StageDefinitions");
        }
    }
}
