using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RepositoryPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    DefaultBaseBranch = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExecutionMode = table.Column<int>(type: "INTEGER", nullable: false),
                    ContainerImage = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Ext4MirrorPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    UseWslForContainers = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    WslDistribution = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BuildTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    TestTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentProfiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    HarnessId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ThinkingEffort = table.Column<int>(type: "INTEGER", nullable: false),
                    ThinkingTokenBudget = table.Column<int>(type: "INTEGER", nullable: true),
                    Temperature = table.Column<double>(type: "REAL", nullable: true),
                    HarnessConfigurationJson = table.Column<string>(type: "TEXT", nullable: true),
                    OptimizationFlags = table.Column<string>(type: "TEXT", nullable: true),
                    PermittedTools = table.Column<string>(type: "TEXT", nullable: true),
                    StageDirectives = table.Column<string>(type: "TEXT", nullable: true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    SecurityTier = table.Column<int>(type: "INTEGER", nullable: false),
                    SystemPrompt = table.Column<string>(type: "TEXT", nullable: true),
                    MaxTurns = table.Column<int>(type: "INTEGER", nullable: true),
                    RoutingTarget = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentProfiles_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MergeMode = table.Column<int>(type: "INTEGER", nullable: false),
                    IsWaitingForHuman = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    InterruptedAgentProfileId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    InterruptedStageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CurrentStageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseBranch = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FeatureBranch = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PipelineDataJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastHangfireJobId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionLogs_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Questions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Rationale = table.Column<string>(type: "TEXT", nullable: false),
                    QuestionType = table.Column<int>(type: "INTEGER", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Answer = table.Column<string>(type: "TEXT", nullable: true),
                    AskedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentProfileId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Questions_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StageCheckpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SerializedMemory = table.Column<string>(type: "TEXT", nullable: false),
                    GitCommitHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageCheckpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StageCheckpoints_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StageExecutionRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StageKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AgentProfileId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    HarnessId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetRouting = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputDataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageExecutionRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StageExecutionRecords_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskDependencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    DependsOnTaskId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskDependencies_Tasks_DependsOnTaskId",
                        column: x => x.DependsOnTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskDependencies_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentProfiles_WorkspaceId",
                table: "AgentProfiles",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionLogs_TaskId",
                table: "ExecutionLogs",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_TaskId",
                table: "Questions",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_StageCheckpoints_TaskId",
                table: "StageCheckpoints",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_StageExecutionRecords_TaskId",
                table: "StageExecutionRecords",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDependencies_DependsOnTaskId",
                table: "TaskDependencies",
                column: "DependsOnTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDependencies_TaskId_DependsOnTaskId",
                table: "TaskDependencies",
                columns: new[] { "TaskId", "DependsOnTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkspaceId",
                table: "Tasks",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentProfiles");

            migrationBuilder.DropTable(
                name: "ExecutionLogs");

            migrationBuilder.DropTable(
                name: "Questions");

            migrationBuilder.DropTable(
                name: "StageCheckpoints");

            migrationBuilder.DropTable(
                name: "StageExecutionRecords");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "TaskDependencies");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Workspaces");
        }
    }
}
