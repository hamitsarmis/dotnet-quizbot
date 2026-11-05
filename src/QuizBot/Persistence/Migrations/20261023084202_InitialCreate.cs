using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuizBot.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BotState",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotState", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Questions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Topic = table.Column<string>(type: "TEXT", nullable: false),
                    Difficulty = table.Column<string>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    Answer = table.Column<string>(type: "TEXT", nullable: false),
                    FollowupsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Questions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AskedQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QuestionId = table.Column<string>(type: "TEXT", nullable: false),
                    AskedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    TelegramMessageId = table.Column<long>(type: "INTEGER", nullable: false),
                    RevealedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Grade = table.Column<string>(type: "TEXT", nullable: true),
                    GradedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    SkippedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AskedQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AskedQuestions_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FireAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    SentAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    AskedQuestionId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledSlots_AskedQuestions_AskedQuestionId",
                        column: x => x.AskedQuestionId,
                        principalTable: "AskedQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AskedQuestions_QuestionId_AskedAtUtc",
                table: "AskedQuestions",
                columns: new[] { "QuestionId", "AskedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledSlots_AskedQuestionId",
                table: "ScheduledSlots",
                column: "AskedQuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledSlots_Status_FireAtUtc",
                table: "ScheduledSlots",
                columns: new[] { "Status", "FireAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotState");

            migrationBuilder.DropTable(
                name: "ScheduledSlots");

            migrationBuilder.DropTable(
                name: "AskedQuestions");

            migrationBuilder.DropTable(
                name: "Questions");
        }
    }
}
