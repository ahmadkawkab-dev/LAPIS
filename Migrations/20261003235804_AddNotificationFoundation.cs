using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "board_notification_preferences",
                columns: table => new
                {
                    board_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<int>(type: "integer", nullable: false),
                    sounds_muted = table.Column<bool>(type: "boolean", nullable: false),
                    muted_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_board_notification_preferences", x => new { x.board_id, x.user_id });
                    table.CheckConstraint("ck_board_notification_preference_mode", "mode BETWEEN 0 AND 2");
                    table.CheckConstraint("ck_board_notification_preference_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "fk_board_notification_preferences_board_memberships_board_id_u",
                        columns: x => new { x.board_id, x.user_id },
                        principalTable: "board_memberships",
                        principalColumns: new[] { "board_id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    in_app_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    push_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    sounds_muted = table.Column<bool>(type: "boolean", nullable: false),
                    sound_volume = table.Column<double>(type: "double precision", nullable: false),
                    chat_notifications_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    task_reminder_notifications_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    scheduled_task_reminder_notifications_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    shared_board_notifications_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    board_invitation_notifications_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    task_activity_notifications_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    chat_sound_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    task_reminder_sound_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    scheduled_task_posted_sound_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    board_invitation_sound_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    task_completed_sound_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    private_previews_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preferences", x => x.user_id);
                    table.CheckConstraint("ck_notification_preference_revision", "revision >= 0");
                    table.CheckConstraint("ck_notification_preference_volume", "sound_volume BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "fk_notification_preferences_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    activity_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    board_id = table.Column<Guid>(type: "uuid", nullable: true),
                    membership_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    read_revision = table.Column<long>(type: "bigint", nullable: false),
                    activity_count = table.Column<int>(type: "integer", nullable: false),
                    aggregation_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    first_chat_sequence = table.Column<long>(type: "bigint", nullable: true),
                    last_chat_sequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.CheckConstraint("ck_notification_board_instance", "(board_id IS NULL AND membership_instance_id IS NULL) OR (board_id IS NOT NULL AND membership_instance_id IS NOT NULL AND membership_instance_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
                    table.CheckConstraint("ck_notification_chat_sequences", "(first_chat_sequence IS NULL AND last_chat_sequence IS NULL) OR (first_chat_sequence IS NOT NULL AND last_chat_sequence IS NOT NULL AND first_chat_sequence > 0 AND last_chat_sequence >= first_chat_sequence)");
                    table.CheckConstraint("ck_notification_revisions", "revision > 0 AND read_revision >= 0 AND read_revision <= revision AND activity_count > 0");
                    table.CheckConstraint("ck_notification_task_reference", "(type = 0 AND task_id IS NOT NULL AND id = task_id) OR (type <> 0 AND task_id IS NULL)");
                    table.CheckConstraint("ck_notification_type", "type BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "fk_notifications_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_personal_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "personal_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Preserve legacy reminder identifiers and read/dismissed state before retiring the old table.
            migrationBuilder.Sql("""
                INSERT INTO notifications
                    (id, task_id, user_id, type, title, resource_kind, resource_id, issued_at, updated_at,
                     read_at, dismissed_at, revision, read_revision, activity_count)
                SELECT old.task_id, old.task_id, old.user_id, 0, task.title, 'task', old.task_id,
                    old.issued_at, old.issued_at, old.read_at, old.dismissed_at, 1,
                    CASE WHEN old.read_at IS NOT NULL OR old.dismissed_at IS NOT NULL THEN 1 ELSE 0 END, 1
                FROM task_notifications AS old JOIN personal_tasks AS task ON task.id = old.task_id
                """);
            migrationBuilder.DropTable(name: "task_notifications");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_issued_at_id",
                table: "notifications",
                columns: new[] { "user_id", "issued_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_board_id_user_id_membership_instance_id",
                table: "notifications",
                columns: new[] { "board_id", "user_id", "membership_instance_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_task_id",
                table: "notifications",
                column: "task_id",
                unique: true,
                filter: "task_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_unread",
                table: "notifications",
                columns: new[] { "user_id", "issued_at", "id" },
                filter: "dismissed_at IS NULL AND read_revision < revision");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_aggregation_key",
                table: "notifications",
                columns: new[] { "user_id", "aggregation_key" },
                unique: true,
                filter: "aggregation_key IS NOT NULL AND dismissed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "board_notification_preferences");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.CreateTable(
                name: "task_notifications",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
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

            migrationBuilder.Sql("""
                INSERT INTO task_notifications (task_id, user_id, issued_at, read_at, dismissed_at)
                SELECT task_id, user_id, issued_at,
                    CASE WHEN read_revision >= revision THEN read_at ELSE NULL END, dismissed_at
                FROM notifications WHERE type = 0 AND task_id IS NOT NULL
                """);
            migrationBuilder.DropTable(name: "notifications");

            migrationBuilder.CreateIndex(
                name: "ix_task_notifications_user_id_issued_at",
                table: "task_notifications",
                columns: new[] { "user_id", "issued_at" });
        }
    }
}
