START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930154807_AddPersonalTasks') THEN
    CREATE TABLE personal_tasks (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        title character varying(200) NOT NULL,
        description character varying(4000),
        planned_date date,
        planned_time time without time zone,
        time_zone_id character varying(100),
        planned_at_utc timestamp with time zone,
        completed_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_personal_tasks PRIMARY KEY (id),
        CONSTRAINT ck_personal_task_schedule CHECK ((planned_time IS NULL AND time_zone_id IS NULL AND planned_at_utc IS NULL) OR (planned_date IS NOT NULL AND planned_time IS NOT NULL AND time_zone_id IS NOT NULL AND planned_at_utc IS NOT NULL)),
        CONSTRAINT fk_personal_tasks_users_user_id FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930154807_AddPersonalTasks') THEN
    CREATE INDEX ix_personal_tasks_user_id_completed_at_planned_date ON personal_tasks (user_id, completed_at, planned_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930154807_AddPersonalTasks') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20260930154807_AddPersonalTasks', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930165025_AddTaskListsAndPlanningSettings') THEN
    ALTER TABLE personal_tasks ADD list_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930165025_AddTaskListsAndPlanningSettings') THEN
    CREATE TABLE personal_task_lists (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        name character varying(80) NOT NULL,
        normalized_name character varying(80) NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_personal_task_lists PRIMARY KEY (id),
        CONSTRAINT fk_personal_task_lists_users_user_id FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930165025_AddTaskListsAndPlanningSettings') THEN
    CREATE TABLE planning_settings (
        user_id uuid NOT NULL,
        time_zone_id character varying(100) NOT NULL,
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_planning_settings PRIMARY KEY (user_id),
        CONSTRAINT fk_planning_settings_asp_net_users_user_id FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930165025_AddTaskListsAndPlanningSettings') THEN
    CREATE INDEX ix_personal_tasks_list_id ON personal_tasks (list_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930165025_AddTaskListsAndPlanningSettings') THEN
    CREATE INDEX ix_personal_tasks_user_id_list_id ON personal_tasks (user_id, list_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930165025_AddTaskListsAndPlanningSettings') THEN
    CREATE UNIQUE INDEX ux_personal_task_lists_user_name ON personal_task_lists (user_id, normalized_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930165025_AddTaskListsAndPlanningSettings') THEN
    ALTER TABLE personal_tasks ADD CONSTRAINT fk_personal_tasks_personal_task_lists_list_id FOREIGN KEY (list_id) REFERENCES personal_task_lists (id) ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930165025_AddTaskListsAndPlanningSettings') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20260930165025_AddTaskListsAndPlanningSettings', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930173527_CascadeTaskListDeletion') THEN
    ALTER TABLE personal_tasks DROP CONSTRAINT fk_personal_tasks_personal_task_lists_list_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930173527_CascadeTaskListDeletion') THEN
    ALTER TABLE personal_tasks ADD CONSTRAINT fk_personal_tasks_personal_task_lists_list_id FOREIGN KEY (list_id) REFERENCES personal_task_lists (id) ON DELETE CASCADE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930173527_CascadeTaskListDeletion') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20260930173527_CascadeTaskListDeletion', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930174224_AddCalendarEvents') THEN
    CREATE TABLE calendar_events (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        title character varying(200) NOT NULL,
        description character varying(4000),
        location character varying(200),
        is_all_day boolean NOT NULL,
        all_day_start_date date,
        all_day_end_date_exclusive date,
        local_start timestamp without time zone,
        local_end timestamp without time zone,
        time_zone_id character varying(100),
        start_at_utc timestamp with time zone,
        end_at_utc timestamp with time zone,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_calendar_events PRIMARY KEY (id),
        CONSTRAINT ck_calendar_event_schedule CHECK ((is_all_day AND all_day_start_date IS NOT NULL AND all_day_end_date_exclusive IS NOT NULL AND all_day_end_date_exclusive > all_day_start_date AND local_start IS NULL AND local_end IS NULL AND time_zone_id IS NULL AND start_at_utc IS NULL AND end_at_utc IS NULL) OR (NOT is_all_day AND all_day_start_date IS NULL AND all_day_end_date_exclusive IS NULL AND local_start IS NOT NULL AND local_end IS NOT NULL AND local_end > local_start AND time_zone_id IS NOT NULL AND start_at_utc IS NOT NULL AND end_at_utc IS NOT NULL AND end_at_utc > start_at_utc)),
        CONSTRAINT fk_calendar_events_users_user_id FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930174224_AddCalendarEvents') THEN
    CREATE INDEX ix_calendar_events_user_id_all_day_start_date_all_day_end_date ON calendar_events (user_id, all_day_start_date, all_day_end_date_exclusive);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930174224_AddCalendarEvents') THEN
    CREATE INDEX ix_calendar_events_user_id_start_at_utc_end_at_utc ON calendar_events (user_id, start_at_utc, end_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260930174224_AddCalendarEvents') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20260930174224_AddCalendarEvents', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261001110733_AddTaskTemplates') THEN
    CREATE TABLE task_templates (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        name character varying(80) NOT NULL,
        items_json text NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_task_templates PRIMARY KEY (id),
        CONSTRAINT fk_task_templates_users_user_id FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261001110733_AddTaskTemplates') THEN
    CREATE INDEX ix_task_templates_user_id ON task_templates (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261001110733_AddTaskTemplates') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261001110733_AddTaskTemplates', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261001165613_AddTaskRemindersAndNotifications') THEN
    CREATE TABLE task_notifications (
        task_id uuid NOT NULL,
        user_id uuid NOT NULL,
        issued_at timestamp with time zone NOT NULL,
        read_at timestamp with time zone,
        dismissed_at timestamp with time zone,
        CONSTRAINT pk_task_notifications PRIMARY KEY (task_id),
        CONSTRAINT fk_task_notifications_personal_tasks_task_id FOREIGN KEY (task_id) REFERENCES personal_tasks (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261001165613_AddTaskRemindersAndNotifications') THEN
    CREATE TABLE task_reminders (
        task_id uuid NOT NULL,
        user_id uuid NOT NULL,
        minutes_before integer NOT NULL,
        due_at_utc timestamp with time zone NOT NULL,
        delivered_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_task_reminders PRIMARY KEY (task_id),
        CONSTRAINT ck_task_reminder_minutes CHECK (minutes_before BETWEEN 0 AND 10080),
        CONSTRAINT fk_task_reminders_personal_tasks_task_id FOREIGN KEY (task_id) REFERENCES personal_tasks (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261001165613_AddTaskRemindersAndNotifications') THEN
    CREATE INDEX ix_task_notifications_user_id_issued_at ON task_notifications (user_id, issued_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261001165613_AddTaskRemindersAndNotifications') THEN
    CREATE INDEX ix_task_reminders_delivered_at_due_at_utc ON task_reminders (delivered_at, due_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261001165613_AddTaskRemindersAndNotifications') THEN
    CREATE INDEX ix_task_reminders_user_id_due_at_utc ON task_reminders (user_id, due_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261001165613_AddTaskRemindersAndNotifications') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261001165613_AddTaskRemindersAndNotifications', '10.0.12');
    END IF;
END $EF$;
COMMIT;

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

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003214259_AddChatTaskCalendarImports') THEN
    ALTER TABLE calendar_events DROP CONSTRAINT ck_calendar_event_schedule;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003214259_AddChatTaskCalendarImports') THEN
    ALTER TABLE calendar_events ADD source_chat_message_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003214259_AddChatTaskCalendarImports') THEN
    CREATE UNIQUE INDEX ix_calendar_events_user_id_source_chat_message_id ON calendar_events (user_id, source_chat_message_id) WHERE source_chat_message_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003214259_AddChatTaskCalendarImports') THEN
    ALTER TABLE calendar_events ADD CONSTRAINT ck_calendar_event_schedule CHECK ((is_all_day AND all_day_start_date IS NOT NULL AND all_day_end_date_exclusive IS NOT NULL AND all_day_end_date_exclusive > all_day_start_date AND local_start IS NULL AND local_end IS NULL AND time_zone_id IS NULL AND start_at_utc IS NULL AND end_at_utc IS NULL) OR (NOT is_all_day AND all_day_start_date IS NULL AND all_day_end_date_exclusive IS NULL AND local_start IS NOT NULL AND time_zone_id IS NOT NULL AND start_at_utc IS NOT NULL AND ((source_chat_message_id IS NULL AND local_end IS NOT NULL AND local_end > local_start AND end_at_utc IS NOT NULL AND end_at_utc > start_at_utc) OR (source_chat_message_id IS NOT NULL AND ((local_end IS NULL AND end_at_utc IS NULL) OR (local_end IS NOT NULL AND end_at_utc IS NOT NULL AND end_at_utc > start_at_utc))))));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003214259_AddChatTaskCalendarImports') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261003214259_AddChatTaskCalendarImports', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    CREATE TABLE board_notification_preferences (
        board_id uuid NOT NULL,
        user_id uuid NOT NULL,
        mode integer NOT NULL,
        sounds_muted boolean NOT NULL,
        muted_until timestamp with time zone,
        revision bigint NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_board_notification_preferences PRIMARY KEY (board_id, user_id),
        CONSTRAINT ck_board_notification_preference_mode CHECK (mode BETWEEN 0 AND 2),
        CONSTRAINT ck_board_notification_preference_revision CHECK (revision >= 0),
        CONSTRAINT fk_board_notification_preferences_board_memberships_board_id_u FOREIGN KEY (board_id, user_id) REFERENCES board_memberships (board_id, user_id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    CREATE TABLE notification_preferences (
        user_id uuid NOT NULL,
        in_app_enabled boolean NOT NULL,
        push_enabled boolean NOT NULL,
        sounds_muted boolean NOT NULL,
        sound_volume double precision NOT NULL,
        chat_notifications_enabled boolean NOT NULL,
        task_reminder_notifications_enabled boolean NOT NULL,
        scheduled_task_reminder_notifications_enabled boolean NOT NULL,
        shared_board_notifications_enabled boolean NOT NULL,
        board_invitation_notifications_enabled boolean NOT NULL,
        task_activity_notifications_enabled boolean NOT NULL,
        chat_sound_enabled boolean NOT NULL,
        task_reminder_sound_enabled boolean NOT NULL,
        scheduled_task_posted_sound_enabled boolean NOT NULL,
        board_invitation_sound_enabled boolean NOT NULL,
        task_completed_sound_enabled boolean NOT NULL,
        private_previews_enabled boolean NOT NULL,
        revision bigint NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_notification_preferences PRIMARY KEY (user_id),
        CONSTRAINT ck_notification_preference_revision CHECK (revision >= 0),
        CONSTRAINT ck_notification_preference_volume CHECK (sound_volume BETWEEN 0 AND 1),
        CONSTRAINT fk_notification_preferences_asp_net_users_user_id FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    CREATE TABLE notifications (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        type integer NOT NULL,
        activity_kind character varying(50),
        title character varying(500) NOT NULL,
        actor_user_id uuid,
        board_id uuid,
        membership_instance_id uuid,
        resource_kind character varying(50),
        resource_id uuid,
        task_id uuid,
        issued_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        read_at timestamp with time zone,
        dismissed_at timestamp with time zone,
        revision bigint NOT NULL,
        read_revision bigint NOT NULL,
        activity_count integer NOT NULL,
        aggregation_key character varying(200),
        first_chat_sequence bigint,
        last_chat_sequence bigint,
        CONSTRAINT pk_notifications PRIMARY KEY (id),
        CONSTRAINT ck_notification_board_instance CHECK ((board_id IS NULL AND membership_instance_id IS NULL) OR (board_id IS NOT NULL AND membership_instance_id IS NOT NULL AND membership_instance_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
        CONSTRAINT ck_notification_chat_sequences CHECK ((first_chat_sequence IS NULL AND last_chat_sequence IS NULL) OR (first_chat_sequence IS NOT NULL AND last_chat_sequence IS NOT NULL AND first_chat_sequence > 0 AND last_chat_sequence >= first_chat_sequence)),
        CONSTRAINT ck_notification_revisions CHECK (revision > 0 AND read_revision >= 0 AND read_revision <= revision AND activity_count > 0),
        CONSTRAINT ck_notification_task_reference CHECK ((type = 0 AND task_id IS NOT NULL AND id = task_id) OR (type <> 0 AND task_id IS NULL)),
        CONSTRAINT ck_notification_type CHECK (type BETWEEN 0 AND 5),
        CONSTRAINT fk_notifications_asp_net_users_user_id FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE,
        CONSTRAINT fk_notifications_personal_tasks_task_id FOREIGN KEY (task_id) REFERENCES personal_tasks (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    INSERT INTO notifications
        (id, task_id, user_id, type, title, resource_kind, resource_id, issued_at, updated_at,
         read_at, dismissed_at, revision, read_revision, activity_count)
    SELECT old.task_id, old.task_id, old.user_id, 0, task.title, 'task', old.task_id,
        old.issued_at, old.issued_at, old.read_at, old.dismissed_at, 1,
        CASE WHEN old.read_at IS NOT NULL OR old.dismissed_at IS NOT NULL THEN 1 ELSE 0 END, 1
    FROM task_notifications AS old JOIN personal_tasks AS task ON task.id = old.task_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    DROP TABLE task_notifications;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    CREATE INDEX ix_notifications_user_id_issued_at_id ON notifications (user_id, issued_at, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    CREATE INDEX ix_notifications_board_id_user_id_membership_instance_id ON notifications (board_id, user_id, membership_instance_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    CREATE UNIQUE INDEX ix_notifications_task_id ON notifications (task_id) WHERE task_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    CREATE INDEX ix_notifications_unread ON notifications (user_id, issued_at, id) WHERE dismissed_at IS NULL AND read_revision < revision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    CREATE UNIQUE INDEX ix_notifications_user_id_aggregation_key ON notifications (user_id, aggregation_key) WHERE aggregation_key IS NOT NULL AND dismissed_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261003235804_AddNotificationFoundation') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261003235804_AddNotificationFoundation', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    ALTER TABLE task_reminders ADD schedule_generation uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    UPDATE task_reminders SET schedule_generation = gen_random_uuid();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    ALTER TABLE notifications ADD reminder_generation uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    ALTER TABLE chat_messages ADD mentions_json jsonb NOT NULL DEFAULT '[]';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    ALTER TABLE chat_messages ADD notify_reply_author boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    ALTER TABLE chat_messages ADD reply_author_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    ALTER TABLE chat_messages ADD reply_to_message_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE TABLE browser_push_subscriptions (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        installation_id uuid NOT NULL,
        endpoint_hash character varying(64) NOT NULL,
        endpoint_protected text NOT NULL,
        p256dh_protected text NOT NULL,
        auth_protected text NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        disabled_at timestamp with time zone,
        failure_count integer NOT NULL,
        CONSTRAINT pk_browser_push_subscriptions PRIMARY KEY (id),
        CONSTRAINT fk_browser_push_subscriptions_asp_net_users_user_id FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE TABLE calendar_event_reminders (
        calendar_event_id uuid NOT NULL,
        user_id uuid NOT NULL,
        minutes_before integer NOT NULL,
        due_at_utc timestamp with time zone NOT NULL,
        schedule_generation uuid NOT NULL,
        delivered_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_calendar_event_reminders PRIMARY KEY (calendar_event_id),
        CONSTRAINT ck_calendar_reminder_minutes CHECK (minutes_before BETWEEN 0 AND 10080),
        CONSTRAINT fk_calendar_event_reminders_calendar_events_calendar_event_id FOREIGN KEY (calendar_event_id) REFERENCES calendar_events (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE TABLE notification_client_presence (
        user_id uuid NOT NULL,
        installation_id uuid NOT NULL,
        tab_id uuid NOT NULL,
        board_id uuid,
        chat_visible boolean NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_notification_client_presence PRIMARY KEY (user_id, installation_id, tab_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE TABLE notification_work (
        id uuid NOT NULL,
        kind integer NOT NULL,
        user_id uuid NOT NULL,
        source_event_key character varying(200) NOT NULL,
        type integer NOT NULL,
        activity_kind character varying(50),
        actor_user_id uuid,
        board_id uuid,
        membership_instance_id uuid,
        resource_kind character varying(50),
        resource_id uuid,
        message_sequence bigint,
        notification_id uuid,
        notification_revision bigint,
        push_subscription_id uuid,
        created_at timestamp with time zone NOT NULL,
        next_attempt_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        processed_at timestamp with time zone,
        lease_token uuid,
        lease_until timestamp with time zone,
        attempts integer NOT NULL,
        CONSTRAINT pk_notification_work PRIMARY KEY (id),
        CONSTRAINT ck_notification_work_attempts CHECK (attempts >= 0),
        CONSTRAINT ck_notification_work_kind CHECK (kind BETWEEN 0 AND 4)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE UNIQUE INDEX ix_browser_push_subscriptions_endpoint_hash ON browser_push_subscriptions (endpoint_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE UNIQUE INDEX ix_browser_push_subscriptions_user_id_installation_id ON browser_push_subscriptions (user_id, installation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE INDEX ix_calendar_event_reminders_due_at_utc ON calendar_event_reminders (due_at_utc) WHERE delivered_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE INDEX ix_calendar_event_reminders_user_id_due_at_utc ON calendar_event_reminders (user_id, due_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE INDEX ix_notification_client_presence_expires_at ON notification_client_presence (expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE INDEX ix_notification_work_next_attempt_at_created_at_id ON notification_work (next_attempt_at, created_at, id) WHERE processed_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    CREATE UNIQUE INDEX ix_notification_work_user_id_source_event_key_kind ON notification_work (user_id, source_event_key, kind);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261004004046_AddNotificationDelivery') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261004004046_AddNotificationDelivery', '10.0.12');
    END IF;
END $EF$;
COMMIT;
