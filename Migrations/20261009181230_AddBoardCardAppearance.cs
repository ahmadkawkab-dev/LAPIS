using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardCardAppearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "card_color",
                table: "boards",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "card_color_version",
                table: "boards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_boards_card_color",
                table: "boards",
                sql: "card_color IS NULL OR card_color IN ('sage', 'blue', 'lavender', 'clay', 'gold', 'rose')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_boards_card_color_version",
                table: "boards",
                sql: "card_color_version >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_boards_card_color",
                table: "boards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_boards_card_color_version",
                table: "boards");

            migrationBuilder.DropColumn(
                name: "card_color",
                table: "boards");

            migrationBuilder.DropColumn(
                name: "card_color_version",
                table: "boards");
        }
    }
}
