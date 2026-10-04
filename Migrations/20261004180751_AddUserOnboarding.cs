using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddUserOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "onboarding_status",
                table: "asp_net_users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotStarted");

            migrationBuilder.AddColumn<int>(
                name: "onboarding_version",
                table: "asp_net_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_onboarding",
                table: "asp_net_users",
                sql: "onboarding_version >= 0 AND onboarding_status IN ('NotStarted', 'Completed', 'Skipped')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_onboarding",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "onboarding_status",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "onboarding_version",
                table: "asp_net_users");
        }
    }
}
