using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddChatTaskCalendarImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_calendar_event_schedule",
                table: "calendar_events");

            migrationBuilder.AddColumn<Guid>(
                name: "source_chat_message_id",
                table: "calendar_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_user_id_source_chat_message_id",
                table: "calendar_events",
                columns: new[] { "user_id", "source_chat_message_id" },
                unique: true,
                filter: "source_chat_message_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_calendar_event_schedule",
                table: "calendar_events",
                sql: "(is_all_day AND all_day_start_date IS NOT NULL AND all_day_end_date_exclusive IS NOT NULL AND all_day_end_date_exclusive > all_day_start_date AND local_start IS NULL AND local_end IS NULL AND time_zone_id IS NULL AND start_at_utc IS NULL AND end_at_utc IS NULL) OR (NOT is_all_day AND all_day_start_date IS NULL AND all_day_end_date_exclusive IS NULL AND local_start IS NOT NULL AND time_zone_id IS NOT NULL AND start_at_utc IS NOT NULL AND ((source_chat_message_id IS NULL AND local_end IS NOT NULL AND local_end > local_start AND end_at_utc IS NOT NULL AND end_at_utc > start_at_utc) OR (source_chat_message_id IS NOT NULL AND ((local_end IS NULL AND end_at_utc IS NULL) OR (local_end IS NOT NULL AND end_at_utc IS NOT NULL AND end_at_utc > start_at_utc)))))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_calendar_events_user_id_source_chat_message_id",
                table: "calendar_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_calendar_event_schedule",
                table: "calendar_events");

            migrationBuilder.DropColumn(
                name: "source_chat_message_id",
                table: "calendar_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_calendar_event_schedule",
                table: "calendar_events",
                sql: "(is_all_day AND all_day_start_date IS NOT NULL AND all_day_end_date_exclusive IS NOT NULL AND all_day_end_date_exclusive > all_day_start_date AND local_start IS NULL AND local_end IS NULL AND time_zone_id IS NULL AND start_at_utc IS NULL AND end_at_utc IS NULL) OR (NOT is_all_day AND all_day_start_date IS NULL AND all_day_end_date_exclusive IS NULL AND local_start IS NOT NULL AND local_end IS NOT NULL AND local_end > local_start AND time_zone_id IS NOT NULL AND start_at_utc IS NOT NULL AND end_at_utc IS NOT NULL AND end_at_utc > start_at_utc)");
        }
    }
}
