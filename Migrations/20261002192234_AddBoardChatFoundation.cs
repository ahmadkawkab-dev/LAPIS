using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wukna.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardChatFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "board_chat_settings",
                columns: table => new
                {
                    board_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slow_mode_seconds = table.Column<int>(type: "integer", nullable: false),
                    settings_revision = table.Column<long>(type: "bigint", nullable: false),
                    last_message_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_board_chat_settings", x => x.board_id);
                    table.CheckConstraint("ck_board_chat_settings_cooldown", "slow_mode_seconds BETWEEN 0 AND 21600");
                    table.CheckConstraint("ck_board_chat_settings_counters", "settings_revision > 0 AND last_message_sequence >= 0");
                    table.ForeignKey(
                        name: "fk_board_chat_settings_boards_board_id",
                        column: x => x.board_id,
                        principalTable: "boards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "board_member_chat_states",
                columns: table => new
                {
                    board_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_muted = table.Column<bool>(type: "boolean", nullable: false),
                    muted_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    moderation_revision = table.Column<long>(type: "bigint", nullable: false),
                    next_send_allowed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cooldown_settings_revision = table.Column<long>(type: "bigint", nullable: false),
                    last_read_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_board_member_chat_states", x => new { x.board_id, x.user_id });
                    table.CheckConstraint("ck_board_member_chat_state_counters", "moderation_revision >= 0 AND cooldown_settings_revision >= 0 AND last_read_sequence >= 0");
                    table.CheckConstraint("ck_board_member_chat_state_instance", "membership_instance_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_board_member_chat_state_mute", "is_muted OR muted_until IS NULL");
                    table.ForeignKey(
                        name: "fk_board_member_chat_states_board_memberships_board_id_user_id",
                        columns: x => new { x.board_id, x.user_id },
                        principalTable: "board_memberships",
                        principalColumns: new[] { "board_id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_blob_work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    board_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    original_storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    preview_storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reserved_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_blob_work", x => x.id);
                    table.CheckConstraint("ck_chat_blob_work_counters", "reserved_bytes >= 0 AND attempts >= 0");
                    table.CheckConstraint("ck_chat_blob_work_lease", "(lease_token IS NULL AND lease_expires_at IS NULL) OR (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)");
                    table.CheckConstraint("ck_chat_blob_work_purpose", "purpose IN (0, 1)");
                    table.CheckConstraint("ck_chat_blob_work_reservation", "purpose <> 0 OR (user_id IS NOT NULL AND client_message_id IS NOT NULL AND reserved_bytes > 0)");
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    board_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    client_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_messages", x => x.id);
                    table.CheckConstraint("ck_chat_message_body", "(type = 0 AND body IS NOT NULL AND length(btrim(body)) BETWEEN 1 AND 4000) OR (type = 1 AND (body IS NULL OR length(body) <= 2000)) OR (type = 2 AND body IS NULL)");
                    table.CheckConstraint("ck_chat_message_client_id", "client_message_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_chat_message_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_chat_message_sequence", "sequence > 0");
                    table.CheckConstraint("ck_chat_message_type", "type IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "fk_chat_messages_boards_board_id",
                        column: x => x.board_id,
                        principalTable: "boards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chat_messages_users_sender_user_id",
                        column: x => x.sender_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chat_outbox_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    board_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    event_version = table.Column<int>(type: "integer", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_sequence = table.Column<long>(type: "bigint", nullable: true),
                    member_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    membership_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attachment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_outbox_events", x => x.id);
                    table.CheckConstraint("ck_chat_outbox_counters", "event_version > 0 AND attempts >= 0 AND (revision IS NULL OR revision >= 0) AND (message_sequence IS NULL OR message_sequence >= 0)");
                    table.CheckConstraint("ck_chat_outbox_kind", "kind IN (0, 1, 2, 3, 4, 5)");
                    table.CheckConstraint("ck_chat_outbox_lease", "(lease_token IS NULL AND lease_expires_at IS NULL) OR (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL AND processed_at IS NULL)");
                    table.CheckConstraint("ck_chat_outbox_payload", "(kind = 0 AND message_id IS NOT NULL AND message_sequence IS NOT NULL AND message_sequence > 0) OR (kind = 1 AND revision IS NOT NULL) OR (kind = 2 AND member_user_id IS NOT NULL AND revision IS NOT NULL) OR (kind = 3 AND attachment_id IS NOT NULL AND message_id IS NOT NULL) OR (kind = 4 AND member_user_id IS NOT NULL AND message_sequence IS NOT NULL) OR (kind = 5 AND ((member_user_id IS NULL AND membership_instance_id IS NULL) OR (member_user_id IS NOT NULL AND membership_instance_id IS NOT NULL)))");
                });

            migrationBuilder.CreateTable(
                name: "chat_attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    preview_storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    input_byte_size = table.Column<long>(type: "bigint", nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    preview_byte_size = table.Column<long>(type: "bigint", nullable: false),
                    stored_byte_size = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    scan_status = table.Column<int>(type: "integer", nullable: false),
                    scan_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_scan_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    scan_lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    scan_lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_attachments", x => x.id);
                    table.CheckConstraint("ck_chat_attachment_attempts", "scan_attempts >= 0");
                    table.CheckConstraint("ck_chat_attachment_available_scan", "scan_status <> 2 OR scanned_at IS NOT NULL");
                    table.CheckConstraint("ck_chat_attachment_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_chat_attachment_lease", "(scan_status = 1 AND scan_lease_token IS NOT NULL AND scan_lease_expires_at IS NOT NULL) OR (scan_status <> 1 AND scan_lease_token IS NULL AND scan_lease_expires_at IS NULL)");
                    table.CheckConstraint("ck_chat_attachment_sizes", "input_byte_size > 0 AND byte_size > 0 AND preview_byte_size > 0 AND stored_byte_size >= byte_size + preview_byte_size");
                    table.CheckConstraint("ck_chat_attachment_status", "scan_status IN (0, 1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "fk_chat_attachments_chat_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "chat_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_chat_tasks",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    starts_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    original_offset_minutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduled_chat_tasks", x => x.message_id);
                    table.CheckConstraint("ck_scheduled_chat_task_end", "ends_at_utc IS NULL OR ends_at_utc > starts_at_utc");
                    table.CheckConstraint("ck_scheduled_chat_task_offset", "original_offset_minutes BETWEEN -840 AND 840");
                    table.CheckConstraint("ck_scheduled_chat_task_title", "length(btrim(title)) > 0");
                    table.CheckConstraint("ck_scheduled_chat_task_zone", "length(btrim(time_zone_id)) > 0");
                    table.ForeignKey(
                        name: "fk_scheduled_chat_tasks_chat_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "chat_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_attachments_message_id",
                table: "chat_attachments",
                column: "message_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_attachments_scan_work",
                table: "chat_attachments",
                columns: new[] { "next_scan_attempt_at", "created_at", "id" },
                filter: "scan_status IN (0, 1, 4)");

            migrationBuilder.CreateIndex(
                name: "ix_chat_blob_work_due_at_id",
                table: "chat_blob_work",
                columns: new[] { "due_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_blob_work_upload_reservation",
                table: "chat_blob_work",
                columns: new[] { "board_id", "user_id", "client_message_id" },
                unique: true,
                filter: "purpose = 0");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_board_id_sender_user_id_client_message_id",
                table: "chat_messages",
                columns: new[] { "board_id", "sender_user_id", "client_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_board_id_sequence",
                table: "chat_messages",
                columns: new[] { "board_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_sender_user_id",
                table: "chat_messages",
                column: "sender_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_outbox_events_pending",
                table: "chat_outbox_events",
                columns: new[] { "next_attempt_at", "created_at", "id" },
                filter: "processed_at IS NULL");

            // Existing boards start in normal mode; existing memberships start
            // with independent, unmuted chat state. No sharing permissions change.
            migrationBuilder.Sql("""
                INSERT INTO board_chat_settings
                    (board_id, slow_mode_seconds, settings_revision, last_message_sequence)
                SELECT id, 0, 1, 0 FROM boards;

                INSERT INTO board_member_chat_states
                    (board_id, user_id, membership_instance_id, is_muted,
                     moderation_revision, cooldown_settings_revision, last_read_sequence)
                SELECT board_id, user_id, gen_random_uuid(), false, 0, 0, 0
                FROM board_memberships;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "board_chat_settings");

            migrationBuilder.DropTable(
                name: "board_member_chat_states");

            migrationBuilder.DropTable(
                name: "chat_attachments");

            migrationBuilder.DropTable(
                name: "chat_blob_work");

            migrationBuilder.DropTable(
                name: "chat_outbox_events");

            migrationBuilder.DropTable(
                name: "scheduled_chat_tasks");

            migrationBuilder.DropTable(
                name: "chat_messages");
        }
    }
}
