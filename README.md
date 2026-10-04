# Wukna

Wukna is a visual workspace for organizing notes and checklists on shared boards. People can arrange ideas on a canvas, connect related notes, and collaborate with board members in real time.

## What Wukna does

- **Shared boards:** Create, search, rename, and delete boards, and invite members with view or edit access.
- **Visual notes:** Arrange colored notes and task lists, edit checklist items, and draw related or prerequisite connections between notes.
- **Live collaboration:** See who is on a board, follow live cursors and editing indicators, and preview drag or resize changes as they happen. Saved changes persist in PostgreSQL.
- **Accounts and profiles:** Register with email and password or sign in with Google, manage sessions, and update a display name, username, avatar, and interface theme.
- **Personal planning:** Manage standalone tasks, lists, templates, calendar events, timed task reminders, and in-app notifications.

## Board navigation

Boards use a finite 12,000 × 8,000 workspace centered on the existing origin (X −6,000 to 6,000, Y −4,000 to 4,000). Cards, panning, and edge auto-pan stop at those boundaries. Navigation follows [Figma's canvas gestures](https://help.figma.com/hc/en-us/articles/15297425105303-Explore-design-files), with a fixed page inspired by [Lucidchart's bounded page settings](https://help.lucid.co/hc/en-us/articles/15578781626772-Adjust-document-and-board-settings).

Each user's latest camera position and zoom are saved separately for each board in browser-local preferences, with a 300 ms debounce and a final save when leaving or refreshing. Returning to a board restores that view, clamped to the current board and viewport sizes. A first visit uses fit-to-content. Preferences remain local to that browser; no new server preference schema is needed.

World dimensions are defined in `frontend/src/features/boards/boardBounds.ts` and enforced by `Features/Board/BoardWorkspaceBounds.cs`; keep both definitions aligned when changing the size. Existing out-of-bounds cards are displayed inside the workspace and saved within bounds on their next geometry edit. Connections and SignalR messages retain their existing world-coordinate contracts.

## What's next

Standalone personal Tasks has a weekly planner, Quick Tasks, reusable day templates, Inbox, Today, Upcoming, named lists, and a saved planning time zone. Calendar has separate events, a combined scheduled task/event read, Month and Agenda views, and date scheduling by drag or date picker. Timed task reminders create in-app notifications. Recurrence and Calendar Week/Day grids are planned separately. Library, Journal, and Pictures still show coming-soon screens; development builds also include sample-content design previews. Board chat has a [responsive panel with text, image, and structured scheduled-task posts, live typing, owner moderation/slow mode, a configurable guest cap, cursor recovery, per-member unread cursors, private attachment storage, durable ClamAV scanning, authorized image previews/downloads, and calendar export](docs/BOARD_CHAT.md). Attachment uploads remain disabled by default pending live OCI and scanner configuration.

## Personal Tasks

Personal tasks are stored separately from board checklists. A task may be unscheduled, date-only, or timed; timed tasks retain their local date, time, IANA time zone, and computed UTC instant. The weekly planner shows dated tasks, including completed ones, in Monday–Sunday boards and keeps unscheduled Quick Tasks separate. Right-clicking a task in the week or calendar opens edit and delete actions; the week day menu can clear all tasks for that day after confirmation. Today includes overdue tasks and separates timed work from anytime work. Users can create, rename, and delete personal task lists; deleting a list also deletes its tasks. The saved planning time zone determines the local date used by Today, Upcoming, and the current week, and defaults to the device zone until saved. Changing it does not alter existing scheduled task times. Templates store reusable titles, descriptions, and optional times; applying one merges new independent task instances into a chosen day. Recurrence is not implemented.

Authenticated task routes are `GET/POST /api/tasks`, `GET/PUT/DELETE /api/tasks/{id}`, `DELETE /api/tasks/day/{date}`, and `POST /api/tasks/{id}/complete|reopen|schedule`. The day deletion route accepts `YYYY-MM-DD`, removes active and completed tasks for that user's date, and returns the deletion count. The schedule command accepts `{ "plannedDate": "YYYY-MM-DD" }` and changes only the date and derived UTC instant. A timed task keeps its local time and time zone; an invalid local time on the new date is rejected. The task query accepts `view=inbox|today|upcoming|all|completed|week`, a local `date` for Today, Upcoming, or the start of a seven-day week, an optional `listId`, and bounded `limit`/`offset` pagination. The week view includes completed tasks. Templates use `GET/POST /api/task-templates`, paged `GET /api/task-templates/page?search=&offset=&limit=`, `GET/PUT/DELETE /api/task-templates/{id}`, and `POST /api/task-templates/{id}/apply` with `{ "plannedDate": "YYYY-MM-DD" }`. Named lists use `GET/POST /api/task-lists` and `PUT/DELETE /api/task-lists/{id}`. The planning zone uses `GET/PUT /api/tasks/settings`. Every operation is scoped to the JWT user.

## Calendar

Calendar events are separate from tasks. All-day events use a start date and an exclusive end date, without an artificial midnight time. Timed events retain authored local start/end values, an IANA time zone, and UTC instants. The Month and Agenda views read both scheduled tasks and events. Calendar also has an unscheduled-task tray: drag tasks onto a day or use Plan to pick a date. Scheduled tasks can be dragged to another visible day or moved with the Agenda date picker. A timed task remains a task when shown in Calendar and retains its authored local time on a date move.

`GET /api/calendar?from=YYYY-MM-DD&to=YYYY-MM-DD&timeZone=IANA&offset=0` accepts an exclusive end date and at most 42 days. It returns user-owned task/event projections, up to 500 per category per page. When `hasMore` is true, the client can request the next page by adding 500 to `offset`. Event CRUD uses `POST /api/calendar/events` and `GET/PUT/DELETE /api/calendar/events/{id}`. All-day event writes provide `allDayStartDate` and `allDayEndDateExclusive`; timed writes provide offset-free `localStart`, `localEnd`, and `timeZoneId`. The server validates local times and rejects daylight-saving gaps and overlaps. Recurring events and event reminders are not exposed.

Scheduled chat cards offer **Add to Wukna Calendar** and **Download .ics**. Adding creates one user-owned calendar snapshot per source message, preserving the existing title, description, timezone, and UTC instants (including an omitted end time). `GET/POST /api/calendar/events/from-chat/{boardId}/{messageId}` checks current board membership and derives ownership from the JWT; concurrent or repeated adds return the same event. The source message ID is retained as provenance without a cascading foreign key, so the calendar entry survives chat-task expiry or source deletion. Downloads continue using the existing authorized ICS export. Apply the `AddChatTaskCalendarImports` migration before running this version.

## Task reminders and notifications

`GET/PUT/DELETE /api/tasks/{id}/reminder` reads, schedules, and cancels a reminder on an open timed task. `PUT` accepts `minutesBefore` from 0 to 10,080. The authored local task time is converted to UTC; the reminder stores the UTC trigger instant. Rescheduling or completing the task updates or cancels its reminder and any stale notification. A PostgreSQL-backed worker polls every 30 seconds and claims up to 100 due reminders per batch with row locks. The reminder and notification transition is atomic and idempotent across restarts or multiple API replicas. Failed database work is logged and retried on the next poll. The database, rather than a process timer, is authoritative.

`GET /api/notifications/page?limit=&cursor=&unreadOnly=` returns active notifications with a cursor, `totalCount`, and `unreadCount`. `GET /api/notifications/unread-count` returns the authenticated user's visible unread count. `GET /api/notifications/upcoming/page?limit=&cursor=` returns scheduled reminders for the next seven days with `totalCount`. The UI loads further pages on demand. `POST /api/notifications/read-all` and `POST /api/notifications/{id}/read|dismiss` manage read/dismiss state; new activity sends its observed `?revision=` so stale actions cannot consume newer updates. Existing reminder IDs remain their task IDs and accept legacy read/dismiss calls. `POST /api/notifications/{taskId}/snooze` accepts 5–1,440 minutes and rearms the persisted reminder. All routes derive ownership from the authenticated JWT. Delivery is in-app; no external notification channel is configured.

The [notification system](docs/NOTIFICATIONS.md) adds persistent chat mentions/replies, per-board modes, realtime activity, supplied foreground sounds and mute controls, saved-calendar reminders, semantic board notifications, and optional encrypted Web Push. Browser opt-in, device removal and account settings are under Account → Preferences & security. `AddNotificationFoundation` preserves legacy reminder state; `AddNotificationDelivery` adds durable delivery, metadata, reminder generations and push storage. Apply reviewed migrations before release; push stays disabled until stable VAPID keys and a contact subject are configured. Publishing and production verification remain operator-controlled.

The `AddPersonalTasks`, `AddTaskListsAndPlanningSettings`, `CascadeTaskListDeletion`, `AddCalendarEvents`, `AddTaskTemplates`, and `AddTaskRemindersAndNotifications` migrations must be applied in order in environments that do not already have them. The cascade migration changes list deletion to delete its tasks; review existing list data before applying it outside local development.

For a production release, review the generated [personal planning migration SQL](ops/migrations/2026-10-02-personal-planning.sql) against the production migration history and database backup, then apply it through the database owner's reviewed procedure before starting the new API image. The SQL is idempotent per migration and was generated from `20260928153353_TrackConnectionVersions` through `20261001165613_AddTaskRemindersAndNotifications`. The standard production workflow deliberately rejects any commit whose `Migrations/` directory differs from the running release; it cannot deploy this release until the migration path and deployment gate are reviewed. The API does not apply migrations on startup.

## Technology

| Area | Technology |
| --- | --- |
| Backend | .NET 10, ASP.NET Core Minimal APIs, Identity, SignalR |
| Frontend | React 19, TypeScript, Vite, Tailwind CSS |
| Data | PostgreSQL, Entity Framework Core, Npgsql |
| Authentication | JWT access tokens, rotating HttpOnly refresh cookies, CSRF protection, Google OAuth |

Wukna is under active development. The features above are implemented; planned areas remain previews or coming-soon screens.

## License

MIT. See [LICENSE](LICENSE).
