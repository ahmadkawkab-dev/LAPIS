using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskListsAndPlanningSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "list_id",
                table: "personal_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "personal_task_lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personal_task_lists", x => x.id);
                    table.ForeignKey(
                        name: "fk_personal_task_lists_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_settings",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_planning_settings", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_planning_settings_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_personal_tasks_list_id",
                table: "personal_tasks",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "ix_personal_tasks_user_id_list_id",
                table: "personal_tasks",
                columns: new[] { "user_id", "list_id" });

            migrationBuilder.CreateIndex(
                name: "ux_personal_task_lists_user_name",
                table: "personal_task_lists",
                columns: new[] { "user_id", "normalized_name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_personal_tasks_personal_task_lists_list_id",
                table: "personal_tasks",
                column: "list_id",
                principalTable: "personal_task_lists",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_personal_tasks_personal_task_lists_list_id",
                table: "personal_tasks");

            migrationBuilder.DropTable(
                name: "personal_task_lists");

            migrationBuilder.DropTable(
                name: "planning_settings");

            migrationBuilder.DropIndex(
                name: "ix_personal_tasks_list_id",
                table: "personal_tasks");

            migrationBuilder.DropIndex(
                name: "ix_personal_tasks_user_id_list_id",
                table: "personal_tasks");

            migrationBuilder.DropColumn(
                name: "list_id",
                table: "personal_tasks");
        }
    }
}
