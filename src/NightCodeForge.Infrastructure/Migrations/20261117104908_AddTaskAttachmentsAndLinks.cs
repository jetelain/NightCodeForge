using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightCodeForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskAttachmentsAndLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    MarkdownPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ContentKind = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WidthPx = table.Column<int>(type: "INTEGER", nullable: true),
                    HeightPx = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceRef = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IsSymlink = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskAttachments_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskAttachmentLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskAttachmentId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAttachmentLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskAttachmentLinks_TaskAttachments_TaskAttachmentId",
                        column: x => x.TaskAttachmentId,
                        principalTable: "TaskAttachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskAttachmentLinks_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachmentLinks_TaskAttachmentId",
                table: "TaskAttachmentLinks",
                column: "TaskAttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachmentLinks_TaskId_TaskAttachmentId",
                table: "TaskAttachmentLinks",
                columns: new[] { "TaskId", "TaskAttachmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_WorkspaceId",
                table: "TaskAttachments",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_WorkspaceId_Sha256",
                table: "TaskAttachments",
                columns: new[] { "WorkspaceId", "Sha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskAttachmentLinks");

            migrationBuilder.DropTable(
                name: "TaskAttachments");
        }
    }
}
