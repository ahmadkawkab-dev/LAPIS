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

