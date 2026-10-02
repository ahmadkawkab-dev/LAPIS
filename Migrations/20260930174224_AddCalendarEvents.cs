using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_all_day = table.Column<bool>(type: "boolean", nullable: false),
                    all_day_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    all_day_end_date_exclusive = table.Column<DateOnly>(type: "date", nullable: true),
                    local_start = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    local_end = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    start_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_events", x => x.id);
                    table.CheckConstraint("ck_calendar_event_schedule", "(is_all_day AND all_day_start_date IS NOT NULL AND all_day_end_date_exclusive IS NOT NULL AND all_day_end_date_exclusive > all_day_start_date AND local_start IS NULL AND local_end IS NULL AND time_zone_id IS NULL AND start_at_utc IS NULL AND end_at_utc IS NULL) OR (NOT is_all_day AND all_day_start_date IS NULL AND all_day_end_date_exclusive IS NULL AND local_start IS NOT NULL AND local_end IS NOT NULL AND local_end > local_start AND time_zone_id IS NOT NULL AND start_at_utc IS NOT NULL AND end_at_utc IS NOT NULL AND end_at_utc > start_at_utc)");
                    table.ForeignKey(
                        name: "fk_calendar_events_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_user_id_all_day_start_date_all_day_end_date",
                table: "calendar_events",
                columns: new[] { "user_id", "all_day_start_date", "all_day_end_date_exclusive" });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_user_id_start_at_utc_end_at_utc",
                table: "calendar_events",
                columns: new[] { "user_id", "start_at_utc", "end_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_events");
        }
    }
}
