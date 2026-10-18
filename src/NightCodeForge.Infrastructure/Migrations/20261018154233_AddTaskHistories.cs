using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskHistories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultWorkflowId",
                table: "Workspaces",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SquashCommitSha",
                table: "Tasks",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "WorkflowId",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowNoTests",
                table: "AgentProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    ConversationType = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    StageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StageKind = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentProfileId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    HarnessId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptFileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Conversations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    MergeMode = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseBranch = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FeatureBranch = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CurrentStageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SerializedDependencies = table.Column<string>(type: "TEXT", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ChangeReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskHistories_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "⚡"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowDefinitions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ConversationTurns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<int>(type: "INTEGER", nullable: false),
                    TurnIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    ThinkingTrace = table.Column<string>(type: "TEXT", nullable: true),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ToolArguments = table.Column<string>(type: "TEXT", nullable: true),
                    ToolResult = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    IsError = table.Column<bool>(type: "INTEGER", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationTurns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationTurns_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StageDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkflowId = table.Column<int>(type: "INTEGER", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "⚙️"),
                    SequenceOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CsharpImplementationType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StageDefinitions_WorkflowDefinitions_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "WorkflowDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkflowId",
                table: "Tasks",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TaskId_StageId_AttemptIndex",
                table: "Conversations",
                columns: new[] { "TaskId", "StageId", "AttemptIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId",
                table: "Conversations",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurns_ConversationId_TurnIndex",
                table: "ConversationTurns",
                columns: new[] { "ConversationId", "TurnIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_StageDefinitions_WorkflowId_SequenceOrder",
                table: "StageDefinitions",
                columns: new[] { "WorkflowId", "SequenceOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_StageDefinitions_WorkflowId_StageId",
                table: "StageDefinitions",
                columns: new[] { "WorkflowId", "StageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskHistories_ChangedAt",
                table: "TaskHistories",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskHistories_TaskId_Version",
                table: "TaskHistories",
                columns: new[] { "TaskId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_Code",
                table: "WorkflowDefinitions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_WorkspaceId",
                table: "WorkflowDefinitions",
                column: "WorkspaceId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_WorkflowDefinitions_WorkflowId",
                table: "Tasks",
                column: "WorkflowId",
                principalTable: "WorkflowDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_WorkflowDefinitions_WorkflowId",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "ConversationTurns");

            migrationBuilder.DropTable(
                name: "StageDefinitions");

            migrationBuilder.DropTable(
                name: "TaskHistories");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "WorkflowDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_WorkflowId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DefaultWorkflowId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "SquashCommitSha",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "AllowNoTests",
                table: "AgentProfiles");
        }
    }
}
