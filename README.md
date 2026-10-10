# Wukna

Wukna is a personal and collaborative workspace designed to bring your ideas, tasks, notes, planning, and saved content into one calm, organized space.

Instead of separating planning, note-taking, collaboration, and personal organization across multiple tools, Wukna brings them together in a visual workspace built around boards, tasks, calendars, and real-time collaboration.

Wukna is currently under active development.

## What Wukna Does

### Visual Boards

Create flexible visual workspaces where ideas can be organized spatially rather than being limited to traditional lists.

Boards support:

- Notes and checklists
- Moveable and resizable cards
- Connections between related ideas
- Board search and organization
- Shared boards
- View and edit permissions
- Persistent canvas positions and zoom
- A bounded workspace designed for comfortable navigation

Your position inside a board is remembered, allowing you to leave a workspace and return to where you were working.

### Real-Time Collaboration

Wukna boards can be shared with other people and edited together.

Collaboration features include:

- Live member presence
- Collaborative editing
- Live cursor and activity indicators
- Editing indicators
- Live card movement and resize previews
- Real-time board updates
- Permission-aware collaboration

The goal is to make shared boards feel active without making the workspace distracting.

### Board Chat

Shared boards include a dedicated conversation space so discussions can stay connected to the work they belong to.

Board chat includes:

- Real-time messaging
- Typing indicators
- Unread messages
- Mentions and replies
- Moderation controls
- Scheduled task and event cards
- Calendar integration

### Personal Tasks

Wukna also includes a personal planning system separate from collaborative board checklists.

You can:

- Create quick tasks
- Organize tasks into custom lists
- Plan tasks across the week
- Schedule tasks for specific dates and times
- Track overdue and upcoming work
- Complete and reopen tasks
- Reuse task templates
- Set reminders
- Manage planning around your local time zone

This keeps personal planning independent from tasks that belong to collaborative boards.

### Calendar

The built-in calendar brings scheduled work together in one place.

It currently supports:

- Month view
- Agenda view
- Scheduled tasks
- Personal calendar events
- All-day and timed events
- Moving tasks between dates
- Planning previously unscheduled tasks
- Adding scheduled items from board conversations
- Calendar reminders
- Calendar export

### Notifications & Reminders

Wukna provides a central notification system for important activity across the workspace.

Notifications can include:

- Task reminders
- Board activity
- Chat mentions and replies
- Calendar reminders
- Collaboration updates

Users can manage notification preferences, unread activity, reminder snoozing, sounds, and supported browser notifications.

### Accounts & Profiles

Wukna supports personal accounts and customizable profiles.

Users can:

- Register with email and password
- Sign in with Google
- Manage active sessions
- Choose a display name and username
- Upload a profile picture
- Configure interface preferences
- Choose their preferred theme
- Explore six coordinated three-family palettes and stable board variations in [Color Studio](docs/COLOR_STUDIO.md)
- Manage notification and security preferences

Profile information is also used throughout collaborative spaces to make it easier to recognize other members.

## What's Planned

Wukna is being developed toward a broader personal workspace rather than only a board and task application.

### Library

A personal space for saving and organizing useful content, including:

- Websites
- Articles
- Videos
- Social media posts
- Study material
- References
- Collections

The goal is to make things you want to revisit part of your workspace instead of leaving them scattered across bookmarks and different platforms.

### Pictures

A dedicated visual library for saving and organizing images.

Planned features include:

- Image collections
- Albums
- Visual browsing
- Organization and categorization
- Integration with the wider Wukna Library

### Journal

A private space for personal writing, thoughts, memories, and daily reflection.

The Journal is planned as a personal part of Wukna rather than a collaborative workspace.

### Planning Improvements

The Tasks and Calendar systems will continue to expand with features such as:

- Recurring tasks
- Recurring events
- Calendar Week view
- Calendar Day view
- Improved long-term planning

## Product Direction

Wukna is built around the idea of having a digital space that feels personal.

The long-term goal is to combine:

- Visual thinking
- Notes
- Tasks
- Planning
- Collaboration
- Journaling
- Saved knowledge
- Personal media

into a workspace that users can shape around the way they think and work.

The focus is on creating something calm, flexible, and personal while still being powerful enough for collaboration.

## Technology

| Area | Technology |
| --- | --- |
| Backend | .NET 10, ASP.NET Core, SignalR |
| Frontend | React 19, TypeScript, Vite, Tailwind CSS |
| Database | PostgreSQL, Entity Framework Core |
| Authentication | ASP.NET Core Identity, JWT, Google OAuth |
| Real-time | SignalR |
| Infrastructure | Docker, Nginx, GitHub Actions |

### Board realtime authorization and deployment

Board authorization coordination supports **one serving API process**. The production Compose file defines one API service, but this is not a live replica-count check. Adding replicas, overlapping serving processes during deployment, or changing membership through out-of-band SQL requires a separately reviewed authorization design. Sticky sessions and a SignalR backplane alone do not propagate the registry's revocation state. Drain the previous serving process before starting its replacement; do not roll back to group-authorized broadcasts while old connections remain active.

Membership changes and board joins share a per-board lifecycle gate, acquired before database locks. Revocation excludes all of the member's registered connections before commit. Sensitive publications select eligible recipients and initiate transport under the registry's short synchronous lock; asynchronous waits occur outside that lock. Publications already initiated before exclusion can finish later. Native SignalR groups never authorize sensitive board data, including cursor, geometry, presence, editing, and background cleanup events.

User-targeted board summaries and membership events use the existing database recipient queries with a membership revision captured before the query and checked at every send. Stale results receive at most one fresh rebuild; validation failure suppresses the affected publication. Intentional `BoardAccessRevoked`, `BoardSummaryRemoved`, and personal profile notifications retain their existing contracts. A re-invitation does not restore old subscriptions: the member must complete a fresh database-authorized join.

Background `NotificationChanged` events containing board data also validate the current membership incarnation under a captured revision and check it at send initiation. This adds one selective visibility query for board notification delivery; personal notifications retain their own authorization. A revision conflict leaves durable notification work available for its existing retry mechanism.

Eligibility is restored only after a verified rollback or settled membership reconciliation. An uncertain outcome remains excluded. Reconciliation uses the existing aggregate database lock only for that recovery path, with a two-second attempt budget, bounded batches, and backoff up to 30 seconds. Routine publications add no recipient database queries or transactions; existing editing permission checks remain.

Group cleanup retains one record per connection/board pair, retries at most five times with backoff, and limits actual outstanding native operations to four. Timed-out operations that ignore cancellation retain their concurrency slot and block reuse of the affected connection ID until they actually settle. Fresh registration generations invalidate obsolete retries; disconnect releases retained records. Cleanup success never grants access.

Compared with locking membership rows for every publication, this approach avoids a database round trip and transaction on each frequent event. It requires lifecycle, revision and registration-generation bookkeeping within one process. A database locking design can coordinate multiple replicas only if every membership writer, join and publication follows compatible locking and subscription-incarnation rules; that would add database load, transport latency and lock contention. Scaling this registry design requires distributed authorization coordination, not merely distributed message transport.

Security tests use real local Production Kestrel/WebSocket connections for normal removal, commit/publication races, multiple tabs, reconnect/rejoin, deletion and downgrade. Native cleanup failures are injected at the component boundary; these tests do not establish actual socket non-delivery under injected group-removal failure. The performance comparison uses real local sockets and PostgreSQL, reports server send completion rather than browser latency, and writes `wukna-board-realtime-performance-*.json` in the test process's temporary directory.

## Project Status

Wukna is under active development.

Boards, real-time collaboration, chat, personal tasks, calendar, notifications, accounts, and profiles form the current application.

Library, Pictures, Journal, recurring planning, and additional calendar experiences are planned as Wukna continues to grow.

## License

MIT — see [LICENSE](LICENSE).
