using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskRemindersAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_notifications",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_notifications", x => x.task_id);
                    table.ForeignKey(
                        name: "fk_task_notifications_personal_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "personal_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_reminders",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    minutes_before = table.Column<int>(type: "integer", nullable: false),
                    due_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_reminders", x => x.task_id);
                    table.CheckConstraint("ck_task_reminder_minutes", "minutes_before BETWEEN 0 AND 10080");
                    table.ForeignKey(
                        name: "fk_task_reminders_personal_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "personal_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_notifications_user_id_issued_at",
                table: "task_notifications",
                columns: new[] { "user_id", "issued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_task_reminders_delivered_at_due_at_utc",
                table: "task_reminders",
                columns: new[] { "delivered_at", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_task_reminders_user_id_due_at_utc",
                table: "task_reminders",
                columns: new[] { "user_id", "due_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_notifications");

            migrationBuilder.DropTable(
                name: "task_reminders");
        }
    }
}
