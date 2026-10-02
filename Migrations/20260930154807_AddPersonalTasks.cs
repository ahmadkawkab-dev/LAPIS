using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "personal_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    planned_date = table.Column<DateOnly>(type: "date", nullable: true),
                    planned_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    planned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personal_tasks", x => x.id);
                    table.CheckConstraint("ck_personal_task_schedule", "(planned_time IS NULL AND time_zone_id IS NULL AND planned_at_utc IS NULL) OR (planned_date IS NOT NULL AND planned_time IS NOT NULL AND time_zone_id IS NOT NULL AND planned_at_utc IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_personal_tasks_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_personal_tasks_user_id_completed_at_planned_date",
                table: "personal_tasks",
                columns: new[] { "user_id", "completed_at", "planned_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "personal_tasks");
        }
    }
}
