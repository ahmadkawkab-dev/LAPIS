using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class CascadeTaskListDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_personal_tasks_personal_task_lists_list_id",
                table: "personal_tasks");

            migrationBuilder.AddForeignKey(
                name: "fk_personal_tasks_personal_task_lists_list_id",
                table: "personal_tasks",
                column: "list_id",
                principalTable: "personal_task_lists",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_personal_tasks_personal_task_lists_list_id",
                table: "personal_tasks");

            migrationBuilder.AddForeignKey(
                name: "fk_personal_tasks_personal_task_lists_list_id",
                table: "personal_tasks",
                column: "list_id",
                principalTable: "personal_task_lists",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
