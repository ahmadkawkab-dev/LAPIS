START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE TABLE board_chat_settings (
        board_id uuid NOT NULL,
        slow_mode_seconds integer NOT NULL,
        settings_revision bigint NOT NULL,
        last_message_sequence bigint NOT NULL,
        CONSTRAINT pk_board_chat_settings PRIMARY KEY (board_id),
        CONSTRAINT ck_board_chat_settings_cooldown CHECK (slow_mode_seconds BETWEEN 0 AND 21600),
        CONSTRAINT ck_board_chat_settings_counters CHECK (settings_revision > 0 AND last_message_sequence >= 0),
        CONSTRAINT fk_board_chat_settings_boards_board_id FOREIGN KEY (board_id) REFERENCES boards (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE TABLE board_member_chat_states (
        board_id uuid NOT NULL,
        user_id uuid NOT NULL,
        membership_instance_id uuid NOT NULL,
        is_muted boolean NOT NULL,
        muted_until timestamp with time zone,
        moderation_revision bigint NOT NULL,
        next_send_allowed_at timestamp with time zone,
        cooldown_settings_revision bigint NOT NULL,
        last_read_sequence bigint NOT NULL,
        CONSTRAINT pk_board_member_chat_states PRIMARY KEY (board_id, user_id),
        CONSTRAINT ck_board_member_chat_state_counters CHECK (moderation_revision >= 0 AND cooldown_settings_revision >= 0 AND last_read_sequence >= 0),
        CONSTRAINT ck_board_member_chat_state_instance CHECK (membership_instance_id <> '00000000-0000-0000-0000-000000000000'::uuid),
        CONSTRAINT ck_board_member_chat_state_mute CHECK (is_muted OR muted_until IS NULL),
        CONSTRAINT fk_board_member_chat_states_board_memberships_board_id_user_id FOREIGN KEY (board_id, user_id) REFERENCES board_memberships (board_id, user_id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE TABLE chat_blob_work (
        id uuid NOT NULL,
        purpose integer NOT NULL,
        board_id uuid NOT NULL,
        user_id uuid,
        client_message_id uuid,
        original_storage_key character varying(200) NOT NULL,
        storage_key character varying(200) NOT NULL,
        preview_storage_key character varying(200) NOT NULL,
        reserved_bytes bigint NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        due_at timestamp with time zone NOT NULL,
        lease_token uuid,
        lease_expires_at timestamp with time zone,
        attempts integer NOT NULL,
        last_error_code character varying(100),
        CONSTRAINT pk_chat_blob_work PRIMARY KEY (id),
        CONSTRAINT ck_chat_blob_work_counters CHECK (reserved_bytes >= 0 AND attempts >= 0),
        CONSTRAINT ck_chat_blob_work_lease CHECK ((lease_token IS NULL AND lease_expires_at IS NULL) OR (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)),
        CONSTRAINT ck_chat_blob_work_purpose CHECK (purpose IN (0, 1)),
        CONSTRAINT ck_chat_blob_work_reservation CHECK (purpose <> 0 OR (user_id IS NOT NULL AND client_message_id IS NOT NULL AND reserved_bytes > 0))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE TABLE chat_messages (
        id uuid NOT NULL,
        board_id uuid NOT NULL,
        sender_user_id uuid NOT NULL,
        sequence bigint NOT NULL,
        type integer NOT NULL,
        body character varying(4000),
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        client_message_id uuid NOT NULL,
        request_fingerprint character varying(64) NOT NULL,
        CONSTRAINT pk_chat_messages PRIMARY KEY (id),
        CONSTRAINT ck_chat_message_body CHECK ((type = 0 AND body IS NOT NULL AND length(btrim(body)) BETWEEN 1 AND 4000) OR (type = 1 AND (body IS NULL OR length(body) <= 2000)) OR (type = 2 AND body IS NULL)),
        CONSTRAINT ck_chat_message_client_id CHECK (client_message_id <> '00000000-0000-0000-0000-000000000000'::uuid),
        CONSTRAINT ck_chat_message_fingerprint CHECK (request_fingerprint ~ '^[0-9a-f]{64}$'),
        CONSTRAINT ck_chat_message_sequence CHECK (sequence > 0),
        CONSTRAINT ck_chat_message_type CHECK (type IN (0, 1, 2)),
        CONSTRAINT fk_chat_messages_boards_board_id FOREIGN KEY (board_id) REFERENCES boards (id) ON DELETE CASCADE,
        CONSTRAINT fk_chat_messages_users_sender_user_id FOREIGN KEY (sender_user_id) REFERENCES asp_net_users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE TABLE chat_outbox_events (
        id uuid NOT NULL,
        board_id uuid NOT NULL,
        kind integer NOT NULL,
        event_version integer NOT NULL,
        message_id uuid,
        message_sequence bigint,
        member_user_id uuid,
        membership_instance_id uuid,
        attachment_id uuid,
        revision bigint,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        next_attempt_at timestamp with time zone NOT NULL DEFAULT (now()),
        attempts integer NOT NULL,
        lease_token uuid,
        lease_expires_at timestamp with time zone,
        processed_at timestamp with time zone,
        last_error_code character varying(100),
        CONSTRAINT pk_chat_outbox_events PRIMARY KEY (id),
        CONSTRAINT ck_chat_outbox_counters CHECK (event_version > 0 AND attempts >= 0 AND (revision IS NULL OR revision >= 0) AND (message_sequence IS NULL OR message_sequence >= 0)),
        CONSTRAINT ck_chat_outbox_kind CHECK (kind IN (0, 1, 2, 3, 4, 5)),
        CONSTRAINT ck_chat_outbox_lease CHECK ((lease_token IS NULL AND lease_expires_at IS NULL) OR (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL AND processed_at IS NULL)),
        CONSTRAINT ck_chat_outbox_payload CHECK ((kind = 0 AND message_id IS NOT NULL AND message_sequence IS NOT NULL AND message_sequence > 0) OR (kind = 1 AND revision IS NOT NULL) OR (kind = 2 AND member_user_id IS NOT NULL AND revision IS NOT NULL) OR (kind = 3 AND attachment_id IS NOT NULL AND message_id IS NOT NULL) OR (kind = 4 AND member_user_id IS NOT NULL AND message_sequence IS NOT NULL) OR (kind = 5 AND ((member_user_id IS NULL AND membership_instance_id IS NULL) OR (member_user_id IS NOT NULL AND membership_instance_id IS NOT NULL))))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE TABLE chat_attachments (
        id uuid NOT NULL,
        message_id uuid NOT NULL,
        original_storage_key character varying(200) NOT NULL,
        storage_key character varying(200) NOT NULL,
        preview_storage_key character varying(200) NOT NULL,
        original_file_name character varying(255) NOT NULL,
        content_type character varying(100) NOT NULL,
        input_byte_size bigint NOT NULL,
        byte_size bigint NOT NULL,
        preview_byte_size bigint NOT NULL,
        stored_byte_size bigint NOT NULL,
        width integer NOT NULL,
        height integer NOT NULL,
        scan_status integer NOT NULL,
        scan_attempts integer NOT NULL,
        next_scan_attempt_at timestamp with time zone NOT NULL DEFAULT (now()),
        scan_lease_token uuid,
        scan_lease_expires_at timestamp with time zone,
        scanned_at timestamp with time zone,
        last_error_code character varying(100),
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_chat_attachments PRIMARY KEY (id),
        CONSTRAINT ck_chat_attachment_attempts CHECK (scan_attempts >= 0),
        CONSTRAINT ck_chat_attachment_available_scan CHECK (scan_status <> 2 OR scanned_at IS NOT NULL),
        CONSTRAINT ck_chat_attachment_dimensions CHECK (width > 0 AND height > 0),
        CONSTRAINT ck_chat_attachment_lease CHECK ((scan_status = 1 AND scan_lease_token IS NOT NULL AND scan_lease_expires_at IS NOT NULL) OR (scan_status <> 1 AND scan_lease_token IS NULL AND scan_lease_expires_at IS NULL)),
        CONSTRAINT ck_chat_attachment_sizes CHECK (input_byte_size > 0 AND byte_size > 0 AND preview_byte_size > 0 AND stored_byte_size >= byte_size + preview_byte_size),
        CONSTRAINT ck_chat_attachment_status CHECK (scan_status IN (0, 1, 2, 3, 4)),
        CONSTRAINT fk_chat_attachments_chat_messages_message_id FOREIGN KEY (message_id) REFERENCES chat_messages (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE TABLE scheduled_chat_tasks (
        message_id uuid NOT NULL,
        title character varying(200) NOT NULL,
        description character varying(4000),
        starts_at_utc timestamp with time zone NOT NULL,
        ends_at_utc timestamp with time zone,
        time_zone_id character varying(100) NOT NULL,
        original_offset_minutes integer NOT NULL,
        CONSTRAINT pk_scheduled_chat_tasks PRIMARY KEY (message_id),
        CONSTRAINT ck_scheduled_chat_task_end CHECK (ends_at_utc IS NULL OR ends_at_utc > starts_at_utc),
        CONSTRAINT ck_scheduled_chat_task_offset CHECK (original_offset_minutes BETWEEN -840 AND 840),
        CONSTRAINT ck_scheduled_chat_task_title CHECK (length(btrim(title)) > 0),
        CONSTRAINT ck_scheduled_chat_task_zone CHECK (length(btrim(time_zone_id)) > 0),
        CONSTRAINT fk_scheduled_chat_tasks_chat_messages_message_id FOREIGN KEY (message_id) REFERENCES chat_messages (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE UNIQUE INDEX ix_chat_attachments_message_id ON chat_attachments (message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE INDEX ix_chat_attachments_scan_work ON chat_attachments (next_scan_attempt_at, created_at, id) WHERE scan_status IN (0, 1, 4);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE INDEX ix_chat_blob_work_due_at_id ON chat_blob_work (due_at, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE UNIQUE INDEX ix_chat_blob_work_upload_reservation ON chat_blob_work (board_id, user_id, client_message_id) WHERE purpose = 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE UNIQUE INDEX ix_chat_messages_board_id_sender_user_id_client_message_id ON chat_messages (board_id, sender_user_id, client_message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE UNIQUE INDEX ix_chat_messages_board_id_sequence ON chat_messages (board_id, sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE INDEX ix_chat_messages_sender_user_id ON chat_messages (sender_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    CREATE INDEX ix_chat_outbox_events_pending ON chat_outbox_events (next_attempt_at, created_at, id) WHERE processed_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    INSERT INTO board_chat_settings
        (board_id, slow_mode_seconds, settings_revision, last_message_sequence)
    SELECT id, 0, 1, 0 FROM boards;

    INSERT INTO board_member_chat_states
        (board_id, user_id, membership_instance_id, is_muted,
         moderation_revision, cooldown_settings_revision, last_read_sequence)
    SELECT board_id, user_id, gen_random_uuid(), false, 0, 0, 0
    FROM board_memberships;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261002192234_AddBoardChatFoundation') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261002192234_AddBoardChatFoundation', '10.0.12');
    END IF;
END $EF$;
COMMIT;
