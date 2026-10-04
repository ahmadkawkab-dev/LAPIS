using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "schedule_generation",
                table: "task_reminders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql("UPDATE task_reminders SET schedule_generation = gen_random_uuid();");

            migrationBuilder.AddColumn<Guid>(
                name: "reminder_generation",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mentions_json",
                table: "chat_messages",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "notify_reply_author",
                table: "chat_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "reply_author_user_id",
                table: "chat_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reply_to_message_id",
                table: "chat_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "browser_push_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    endpoint_protected = table.Column<string>(type: "text", nullable: false),
                    p256dh_protected = table.Column<string>(type: "text", nullable: false),
                    auth_protected = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_browser_push_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_browser_push_subscriptions_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "calendar_event_reminders",
                columns: table => new
                {
                    calendar_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    minutes_before = table.Column<int>(type: "integer", nullable: false),
                    due_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    schedule_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_event_reminders", x => x.calendar_event_id);
                    table.CheckConstraint("ck_calendar_reminder_minutes", "minutes_before BETWEEN 0 AND 10080");
                    table.ForeignKey(
                        name: "fk_calendar_event_reminders_calendar_events_calendar_event_id",
                        column: x => x.calendar_event_id,
                        principalTable: "calendar_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_client_presence",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tab_id = table.Column<Guid>(type: "uuid", nullable: false),
                    board_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chat_visible = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_client_presence", x => new { x.user_id, x.installation_id, x.tab_id });
                });

            migrationBuilder.CreateTable(
                name: "notification_work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    activity_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    board_id = table.Column<Guid>(type: "uuid", nullable: true),
                    membership_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_sequence = table.Column<long>(type: "bigint", nullable: true),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notification_revision = table.Column<long>(type: "bigint", nullable: true),
                    push_subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_work", x => x.id);
                    table.CheckConstraint("ck_notification_work_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_notification_work_kind", "kind BETWEEN 0 AND 4");
                });

            migrationBuilder.CreateIndex(
                name: "ix_browser_push_subscriptions_endpoint_hash",
                table: "browser_push_subscriptions",
                column: "endpoint_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_browser_push_subscriptions_user_id_installation_id",
                table: "browser_push_subscriptions",
                columns: new[] { "user_id", "installation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_event_reminders_due_at_utc",
                table: "calendar_event_reminders",
                column: "due_at_utc",
                filter: "delivered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_event_reminders_user_id_due_at_utc",
                table: "calendar_event_reminders",
                columns: new[] { "user_id", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_client_presence_expires_at",
                table: "notification_client_presence",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_notification_work_next_attempt_at_created_at_id",
                table: "notification_work",
                columns: new[] { "next_attempt_at", "created_at", "id" },
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_work_user_id_source_event_key_kind",
                table: "notification_work",
                columns: new[] { "user_id", "source_event_key", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "browser_push_subscriptions");

            migrationBuilder.DropTable(
                name: "calendar_event_reminders");

            migrationBuilder.DropTable(
                name: "notification_client_presence");

            migrationBuilder.DropTable(
                name: "notification_work");

            migrationBuilder.DropColumn(
                name: "schedule_generation",
                table: "task_reminders");

            migrationBuilder.DropColumn(
                name: "reminder_generation",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "mentions_json",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "notify_reply_author",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "reply_author_user_id",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "reply_to_message_id",
                table: "chat_messages");
        }
    }
}
