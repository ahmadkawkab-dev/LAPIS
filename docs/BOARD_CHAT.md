# Board chat implementation status

The approved board-chat architecture is being implemented incrementally. The
current implementation includes the PostgreSQL/EF Core foundation, authenticated
HTTP history/text-send and scheduled-task creation endpoints, a dedicated ChatHub, an outbox dispatcher,
a responsive frontend chat panel with cursor recovery and structured scheduled-task posting, ephemeral typing,
owner mute/unmute and slow-mode commands, and a configurable guest cap. The
attachment backend now supports private development storage, bounded image
uploads, real clamd scans, durable recovery/cleanup, and authorized downloads.
An opt-in private OCI Object Storage adapter is implemented for production; live
bucket/IAM verification
remain. The attachment UI is implemented. Attachments default to disabled; enabling local storage outside
Development fails startup. Chat revocation is
integrated into membership removal and board deletion. Existing Board Hub, auth,
canvas, sharing permissions, profile, personal-task, and calendar behavior is
preserved.

## Persistence foundation

`Features/Chat/` contains seven entities and their explicit EF configurations:

| Entity | Responsibility |
| --- | --- |
| `BoardChatSettings` | Normal/slow-mode settings, settings revision, and per-board sequence counter |
| `BoardMemberChatState` | Mute/cooldown/read state belonging to an existing membership |
| `ChatMessage` | Typed text, attachment, or scheduled-task post with sender and client operation ID |
| `ChatAttachment` | Private object identifiers, byte accounting, scan state, and recoverable scan lease |
| `ScheduledChatTask` | Structured title/description and UTC scheduling with authored time-zone context |
| `ChatOutboxEvent` | Typed publication references, retry timing, and recoverable dispatcher lease |
| `ChatBlobWork` | Durable upload reservations and object deletion work |

All tables follow PostgreSQL snake-case naming. Message sequences are unique per
board. `(board_id, sender_user_id, client_message_id)` is unique for retry
idempotency. Message fingerprints contain a canonical request SHA-256 hex digest;
text sends compare it when an operation ID is reused.

Member chat state has a cascading composite FK to `BoardMembership`. Removing a
membership therefore removes its operational chat state. A fresh membership
instance ID is generated when new state is created, including re-invitation.
Historical messages reference the user, not the membership; user deletion is
restricted rather than silently deleting history.

Board deletion cascades through settings, messages, attachments, and scheduled
tasks. Outbox and blob-work references deliberately do not have cascading FKs, so
work survives deletion of domain rows. The board-deletion endpoint now enqueues
blob cleanup inside its deletion transaction, before metadata cascades.

Scan states are Pending, Scanning, Available, Rejected, and ScanFailed. Scanning
requires a token and expiry; Available requires a scan-completion timestamp. These
constraints are reinforced by the attachment worker and authorized download route.

Outbox and blob leases require both token and expiry, or neither. An acknowledged
outbox record cannot retain a lease. Partial indexes support bounded pending-work
queries. Outbox, blob cleanup, and scan lease expiry/reclaim and conditional
acknowledgement are implemented. Message-dependent type matching and complete payload construction remain
command-layer requirements; a one-to-one FK does not guarantee that a message has
a dependent row.

## Migration and deployment

`20261002192234_AddBoardChatFoundation` creates the seven tables and backfills
settings/member state for existing boards/members. Backfill starts in normal mode,
with no mute/cooldown/read progress, and preserves existing sharing permissions.
It does not create messages or modify earlier migrations. Newly created boards
receive default normal-mode settings on their first chat operation; new memberships
receive chat state on their first successful send, join, state read, or owner moderation-list read. Both are initialized
inside a transaction, so concurrent initialization cannot create duplicate state.

The migration is generated and reviewed locally and exercised only in isolated
integration-test databases. It is not applied to development or production
databases by this step. Production remains subject to the existing reviewed
migration/deployment procedure; the standard deployment workflow rejects migration
differences. The API does not apply migrations on startup.

The idempotent [chat migration SQL](../ops/migrations/2026-10-03-board-chat.sql)
is prepared for that procedure. It starts after
`20261001165613_AddTaskRemindersAndNotifications`; verify the target database's
`__EFMigrationsHistory` and backup first, and apply any missing personal-planning
migrations before this script. The chat script creates seven tables and indexes,
then backfills one settings row per existing board and one chat-state row per
existing membership. It has not been applied to a live database.

## HTTP history and text sends

All routes use the existing JWT authentication and current `BoardMembership`.
Canvas `CanEdit` does not control chat: invited read-only guests can chat.
Unrelated or removed users receive 404 for board-scoped resources. Responses use
`Cache-Control: no-store`; sender profiles include username, display name, and
the existing avatar URL/version, without email or storage object keys.

| Route | Contract |
| --- | --- |
| `GET /api/boards/{boardId}/chat/messages` | Latest, older, or forward catch-up page |
| `GET /api/boards/{boardId}/chat/messages/{messageId}` | One message scoped to the authorized board |
| `POST /api/boards/{boardId}/chat/messages` | Durable text send with `{ clientMessageId, body }` |

History accepts `limit` (1–100, default 50), `before`, `after`, and `through`.
`before` and `after` are mutually exclusive; `through` requires `after`.
Items always arrive in ascending sequence order. `HasMore` indicates more items
in the requested direction. Use `OlderCursor` with `before` to prepend older
history. For reconnect, use `NewerCursor` with `after`; while `HasMore` is true,
retain the first page's `CatchUpThrough` as `through` to finish that fixed batch.
Start a fresh pass without `through` to recover messages arriving during catch-up.
An empty board still returns a valid zero-sequence `NewerCursor`.

Cursors are opaque, authenticated, board-bound ASP.NET Core Data Protection
tokens. Sequence values are decimal strings in JSON to avoid JavaScript integer
precision loss. Ordering uses the unique per-board sequence, so equal timestamps
do not cause skipped/duplicate pages. The existing persisted Data Protection key
ring must be retained across application restarts for old cursors to remain valid.

Text bodies must be nonblank and at most 4,000 UTF-16 code units before trimming;
outer whitespace is trimmed and CRLF becomes LF. Text remains plain text and
is rendered as React text nodes by the chat UI. Sender identity comes exclusively
from JWT claims. JSON is limited to 32 KiB before deserialization, including
requests without `Content-Length`.

A new send returns 201 with `Message`, `ServerTime`, `NextSendAllowedAt`, and
`IsReplay=false`, and `SettingsRevision`. An identical operation retry returns 200 with the existing
message and `IsReplay=true`; reuse with different normalized content returns
409 (`chat_operation_conflict`). Current membership is required for replays.
Replays create no new outbox record and consume no new cooldown slot.

Persisted mutes block new sends with 403 (`chat_muted`) while allowing reads and
replays. Mute errors include server time, mute revision, expiry, and membership
instance; a delayed rejection cannot reapply an older mute after an unmute.
Persisted slow mode returns 429 (`chat_cooldown`) with server time, next allowed
send time, settings revision, and `Retry-After`. Owners bypass slow mode; normal mode
has no cooldown. Cooldown revisions prevent reusing a stale settings policy.
Owner commands below administer these values through authenticated HTTP.

The transaction takes a board key-share lock, settings update lock, membership
share lock, and member-state update lock in that order. Settings serialize board
sequence assignment and concurrent sends. Membership removal cannot commit while
a send uses its authorization. The message, sequence, cooldown, and outbox event
commit together; a failure rolls all four back. Chat does not update canvas board
activity timestamps. The background dispatcher publishes committed outbox references
through the dedicated ChatHub.

`Chat:MessageSendsPerMinute` defaults to 30 (valid range 1–10,000), configurable
through configuration or `Chat__MessageSendsPerMinute`. The HTTP fixed-window
limit is per authenticated user across boards, includes retries/rejected sends,
and returns 429 (`chat_rate_limited`) plus `Retry-After`. It is separate from
slow mode, applies to owners too, and is process-local for the approved single
API deployment. No request queue is used.

## Owner moderation and guest limit

| Route | Contract |
| --- | --- |
| `GET /api/boards/{boardId}/chat/state` | Current member's effective mute, revisions, owner flag, cooldown, and server clock; same shape as JoinBoard |
| `GET /api/boards/{boardId}/chat/members?afterUserId=...` | Owner-only public profiles and moderation state; at most 100 items with `nextUserId` |
| `PUT /api/boards/{boardId}/chat/settings` | Owner-only `{slowModeSeconds, expectedRevision}`; 0–21,600 seconds, 0 is normal mode |
| `PUT /api/boards/{boardId}/chat/members/{memberId}/mute` | Owner-only `{isMuted, mutedUntil, membershipInstanceId, expectedRevision}`; null expiry is permanent; timed expiry must be future; unmute requires null expiry |
| `GET /api/boards/{boardId}/guest-limit` | Any current member: `{maxGuests, guestCount}`; no-store |

Owner controls use existing HTTP authentication and board membership. Canvas edit
permission grants no moderation privilege. Owner mute attempts return 409
(`chat_owner_protected`). Unknown target members return 404. Changed desired-state
PUTs require the current revision; stale settings/mute return 409
(`chat_settings_conflict` / `chat_moderation_conflict`). A command for an old
membership instance returns 409 (`chat_membership_changed`). Repeating the current
desired state is safe after a lost reply and does not add a revision/outbox event.
Domain changes and their reference events commit in one transaction. Settings
changes reset previous-revision cooldowns on the next read/send. Moderation does
not update board activity timestamps.

The owner's chat-settings view provides slow mode, permanent/one-hour/one-day
mute, and unmute. It fetches current member state and refreshes after success,
conflict, or a lost reply; it does not assume the mutation succeeded. Guests see
only messages and their own effective mute/countdown. Server checks remain
authoritative. State responses cannot roll back newer settings/mute revisions;
send responses from an older settings revision cannot restore a cleared cooldown.

`Board:MaxGuests` defaults to **20 guests plus the owner**, with valid range 0–1,000.
Change it in `appsettings.json` or set `Board__MaxGuests`. Reloadable configuration
is read through `IOptionsMonitor` for each invitation/read. Environment-variable
changes require restarting the process. Lowering the cap preserves existing
members and rejects new invitations until the count is below the cap; a cap of
zero forbids new guests. Permission updates for existing guests remain allowed
when full/over-limit. Only new memberships consume guest capacity.

Invitation, permission change, and removal lock the board `FOR NO KEY UPDATE`
before membership locks. The count/check/insert share one transaction, so
concurrent invitations cannot exceed the cap. This lock is compatible with chat's
board key-share lock. Existing invitations remain the only membership system.
Full boards return 409 (`board_guest_limit_reached`) with `maxGuests`/`guestCount`.
The sharing panel displays the cap without disabling updates for existing guests.

`Chat:ModerationWritesPerMinute` and `Board:MembershipWritesPerMinute` each default
to 30 (valid range 1–10,000). Fixed-window policies are per authenticated user
across boards, separate from message sends and slow mode, and include owners.
They return 429 (`chat_moderation_rate_limited` / `board_membership_rate_limited`)
with `Retry-After` and no-store. Like the existing send limit, these are process-local
and have no request queue; changing an already-created limiter requires restart.

Client typing starts only from active input, renews at most every 2.5 seconds,
stops after three seconds without input, and stops on empty draft, send, blur,
close, hidden tab, disconnect, mute, or unmount. It is kept only in memory.

## ChatHub and publication

`/hubs/chat` is separate from `/hubs/board`, with its own connection registry and
`chat:{boardId}` groups. It uses the existing JWT authentication, subject user-ID
provider, and token-expiry disconnect setting. Browser query-token fallback is
accepted only on the two hub paths. Ordinary APIs continue to require headers.

| Hub method | Contract |
| --- | --- |
| `JoinBoard(boardId)` | Verify current membership; initialize state; subscribe this connection; return board ID, membership instance ID, latest cursor, slow-mode settings/revision, effective mute/revision, next allowed send time, server time, and owner flag |
| `LeaveBoard(boardId)` | Remove this connection's subscription/group and stop its typing lease |
| `SetTyping(boardId, active)` | Check subscription and current membership instance; reject active typing when muted; publish an ephemeral lease |

There is no durable send hub method. Group operations allow 60 invocations per
connection per minute and at most eight board subscriptions per connection.
Excess calls return `chat_hub_rate_limited`; excess subscriptions return
`chat_subscription_limit`. Invalid membership returns `chat_forbidden`.
Disconnect removes connection state. These transport limits supplement the
authenticated HTTP send limit. Typing has its own 120-invocation/connection/minute
window and `chat_typing_rate_limited` rejection; it does not consume group-operation
allowances. Active renewals publish at most once every two seconds per connection/board.
Leases expire after eight seconds and are not written to PostgreSQL or the outbox.
Stop, leave, disconnect, revocation, and effective mute clear matching leases. Failed
cleanup falls back to expiry. Membership and chat-state share locks remain held
through the enqueue, so removal/mute cannot commit during active typing authorization.

| Server event | Payload |
| --- | --- |
| `ChatMessageCreated` | `eventId`, `eventVersion=1`, `boardId`, `messageId`, decimal-string `sequence` |
| `ChatAccessRevoked` | `eventId`, `eventVersion=1`, `boardId`, revoked `membershipInstanceId` |
| `ChatSettingsChanged` | `eventId`, `eventVersion=1`, `boardId`, decimal-string `revision`; all current subscribers |
| `ChatMemberStateChanged` | `eventId`, `eventVersion=1`, `boardId`, `memberUserId`, `membershipInstanceId`, decimal-string `revision`; owner and affected member only |
| `ChatTypingChanged` | `eventVersion=1`, `boardId`, `connectionId`, `userId`, `membershipInstanceId`, decimal-string `sequence`, `isTyping`, `expiresAt`, public `sender` profile (null on stop) |

Durable reference events carry no message body, attachment keys, or scheduling content.
Settings/member references trigger an authorized state read; delayed events cannot
replace current state or apply an old membership's mute. Typing sequences are
monotonic per connection; clients retain bounded stop tombstones, expire leases,
and aggregate multiple tabs by user. One tab stopping does not hide another tab. On a message
event, run forward history catch-up; an authorized single-message fetch may also
reconcile a pending send, but must not advance the catch-up cursor past unseen
sequences. Advance that cursor only from ordered HTTP history pages. Delivery is
at least once: deduplicate event IDs and merge authoritative message IDs.
Ordering of notifications is not guaranteed; history sequence is authoritative.
No subscribers is a successful publication attempt: reconnect always recovers
through durable HTTP history.

Groups are transport bookkeeping, not publication authorization. Before enqueueing
message references, the publisher verifies the message's board/ID/sequence and
locks current board memberships and chat states `FOR SHARE`. It filters subscribed connections
against the current member-state instance, then enqueues through explicit connection
IDs. Membership locks stay held through publication and acknowledgement. Removal
cannot commit midway through that authorization. An already enqueued event can
arrive later; subsequent HTTP access still independently checks membership.

The dispatcher claims one due row at a time using PostgreSQL `FOR UPDATE SKIP
LOCKED`, commits its lease, then publishes and acknowledges in a separate
transaction. Attempts increment on claim. Acknowledgement requires the matching,
unexpired token; stale workers cannot acknowledge reclaimed work. Failure releases
the lease conditionally and persists exponential retry timing from two seconds up
to five minutes. A crash leaves a reclaimable expired lease. If failure occurs
after enqueue but before acknowledgement, retry uses the same event ID. No broker,
Redis, or new package is required.

MessageCreated, AccessRevoked, SettingsChanged, and MemberStateChanged outbox
kinds/version 1 are published. Future read-state and attachment commands must add
their publication handlers. Unsupported kinds/versions fail closed and remain
pending with retry logging. Processed records currently remain in PostgreSQL;
retention is not implemented.

Options use `Chat:Outbox` (`Chat__Outbox__...` environment variables):

| Option | Default | Valid values |
| --- | --- | --- |
| `Enabled` | `true` | Boolean; disabling pauses polling, without losing pending work |
| `PollMilliseconds` | `1000` | 100–60,000 |
| `BatchSize` | `20` | 1–100 claims per batch |
| `LeaseSeconds` | `30` | 10–300; at least twice the publication timeout |
| `PublicationTimeoutSeconds` | `5` | 1–30; bounds claim, publication/acknowledgement, and retry-state I/O separately |

## Removal and recovery

Guest removal locks the board membership aggregate, then the target membership
before capturing its chat-state instance. Deletion and AccessRevoked insertion commit together. A best-effort
post-commit eviction notifies existing connections promptly; its outbox record
provides retry recovery if transport fails. The HTTP removal still succeeds after
a committed deletion if immediate notification fails. Board deletion records a
board-wide revocation in its transaction; that event survives the board cascade.
Pending message events for deleted boards are safely acknowledged without sending.

Removal excludes the old membership from message publication immediately at the
database boundary, even if its group/registry eviction is delayed. Re-invitation
creates a fresh member-state instance: old subscriptions do not receive new
messages. A delayed member revocation removes only its captured old instance.
The client ignores revocations for a different current instance.

Reconnect flow: authenticate, call `JoinBoard` again, then request HTTP history
after the last cursor established by that client's ordered history paging. Do not overwrite the saved
cursor with the join response's latest cursor before fetching missed messages.
Complete fixed-boundary paging and merge by message ID. Subscriptions do not
survive disconnect or process restart; history and outbox work do.

The Nginx `/hubs/` route already forwards WebSocket upgrades with a 3,600-second
read timeout and covers `/hubs/chat`. Its access log is now disabled to avoid
logging browser `access_token` query strings. Application ASP.NET request logs
already use Warning level. Connect/disconnect, join rejection, revocation, and
publication retry logs use identifiers/failure types without chat content or tokens.
This is a local configuration review; the deployed proxy and external ingress
have not yet been exercised. Multiple API replicas still require SignalR scale-out;
PostgreSQL lease safety alone does not distribute process-local subscriptions.

## Frontend panel and recovery

The existing board toolbar opens chat. Desktop chat overlays the right side of
the board without changing canvas dimensions or camera state. At widths of
900px or less, the same content uses the shared native modal as a full-height
sheet with focus containment, Escape dismissal, and safe-area padding. The
canvas and BoardHub retain their own state and connection; incoming chat
messages and draft edits update only the chat subtree.

The chat connection starts on first open, stays active across panel close/reopen,
and stops on board/session unmount. It shares the existing in-memory JWT token
and `apiFetch` authentication lifecycle. Reconnect and initial-connection retries
revalidate membership through `JoinBoard`. A visible-page interval (30 seconds),
focus, and visibility restoration also request recovery. HTTP requests have a
15-second timeout and cancel on unmount/revocation.

Only ordered HTTP history pages advance recovery progress. A join head hint,
out-of-order/duplicate SignalR notification, or successful individual send
response cannot acknowledge intervening messages. Forward pagination retains
the server's fixed `catchUpThrough` boundary until that batch is complete.
Recovery yields after ten pages and continues the same batch in another job.
Earlier-history pagination has an independent cursor and preserves the visible
row. At most 500 confirmed messages are kept in memory. Reading older history
pins that window; a latest-messages action reloads the head when newer messages
were trimmed. Messages merge by authoritative ID and compare decimal long
sequences with `BigInt`.

Sending uses HTTP with a generated operation ID. Failed or uncertain sends
remain visible with retry/discard controls. Retry preserves both the original
body and operation ID. History can confirm a send whose HTTP reply was lost,
without duplicating it. Only this user's matching operation can confirm their
pending item. Text renders through React text nodes, including literal HTML.
Enter sends, Shift+Enter inserts a newline, and IME composition does not submit.
The panel displays existing persisted mute/slow-mode enforcement; countdowns
are informative and use server time. Owners and read-only guests use the same
chat interface, with authorization enforced by the server.

Drafts use versioned, user/board-scoped `sessionStorage`, bounded to 4,000
characters. Disabled/malformed storage falls back to memory. Drafts survive
normal close/reopen and same-tab reload. Confirmed messages and tokens are not
stored there. Pending sends and scroll position remain local in memory;
pending operations do not survive a page reload. Revocation clears this board's
cached messages, pending sends, and draft, and invalidates late request results.
Revocation events match the current membership instance, so delayed events from
an earlier invitation cannot clear a new membership. The new-message badge now
uses the per-member server cursor described below; it does not create
per-message read receipts.

## Verification

- Release build: passed with zero warnings/errors.
- Focused PostgreSQL schema tests: 16 passed, none failed/skipped.
- Focused HTTP chat tests: 16 passed, none failed/skipped.
- Focused realtime/outbox and registry tests: 17 passed, none failed/skipped.
- Full backend suite after typing/moderation/guest-cap changes: 124 passed, none
  failed/skipped, including existing Board Hub, authentication, board, personal-task, and calendar behavior.
- EF pending-model check: no differences from the generated snapshot.
- Generated idempotent SQL reviewed: new tables/backfill only; deliberate FKs,
  unique/partial indexes, and transaction boundaries.
- `git diff --check`: passed.

Schema tests cover existing-data backfill, operation-ID scope and sequence
uniqueness, membership removal/re-invitation, retained historical senders,
cascades and surviving operational records, transaction rollback, and invalid
record rejection. HTTP tests additionally cover membership/board isolation,
forged sender input, read-only guests, removal during an in-flight send,
idempotency, atomic cooldown, mute expiry, cursor paging/reload/catch-up,
initialization, byte limits, per-user rate limits, and rollback after SQL writes.
Realtime tests cover two authenticated WebSocket clients, long-polling clients,
query-token scope, publication references, board isolation, failed/stale eviction,
re-invitation, reconnect catch-up, hosted polling, timeout/backoff/restart recovery,
duplicate notifications, concurrent claims, expired token rejection, atomic
removal rollback, publication/removal ordering, and board deletion. At that
text-chat checkpoint, scanning and storage had not yet been verified; their
later coverage appears below. Deployed ingress remains unverified.

Frontend step checks:

- 22 focused controller/transport/browser-timer cases passed: drafts, long sequences,
  join/load races, fixed catch-up boundaries, send/history ordering, lost replies,
  immutable retries, concurrent submit prevention, sender isolation, clock skew,
  older pagination, bounded windows, stale results, membership-instance
  revocation, initial retry, reconnect, event scoping, disposal, and native
  browser timer receiver handling.
- Full frontend suite after typing/moderation/guest-cap changes: 154 individual
  cases passed using
  `node --test --experimental-test-isolation=none tests/*.test.mjs`. The sandbox's
  default subprocess-isolated runner reported only file-level results; the
  additional run verifies the individual cases.
- TypeScript check and Vite production build passed. Vite reports the existing
  SignalR annotation notices and a bundle-size warning; no dependencies changed.
- Current backend and integration-test Release builds passed with zero warnings/errors.
  The full 124-case backend suite was rerun for the moderation step.
- Browser checks use two isolated sessions and a disposable PostgreSQL database,
  with current reviewed migrations applied only to that test database. Checked
  owner/read-only guest sends, live delivery, reload persistence, literal HTML
  rendering, draft close/reopen, focus restoration, desktop canvas dimensions,
  full-height mobile layout at 390×844 in light/dark themes, no horizontal overflow, native modal
  focus, Escape dismissal, and HTTP denial/UI removal after guest removal.
  A simulated guest outage prevented loading a new saved message; restoring
  connectivity recovered it through HTTP catch-up without reloading the panel.
  Browser diagnostics found a native timer receiver bug; default timers now use
  wrapper functions. Repeated outage testing recovered both history and live
  realtime, with no additional browser exceptions after that fix.

## Files changed in the realtime step

- Added `Features/Chat/ChatHub.cs`, `ChatConnectionRegistry.cs`,
  `ChatRealtimePublisher.cs`, `ChatOutboxDispatcher.cs`, and `ChatRevocation.cs`.
- Updated `Features/Chat/ChatEndpoints.cs` for service/worker options,
  `Program.cs` for hub mapping and scoped query-token authentication,
  `Features/Board/BoardEndpoints.cs` for transactional revocation, and
  `nginx.conf` for hub token-log protection.
- Added `tests/Wukna.IntegrationTests/ChatRealtimeTests.cs` and
  `ChatConnectionRegistryTests.cs`; updated `WuknaWebApplicationFactory.cs` to
  make polling explicit in tests and `ChatMessageEndpointTests.cs` to observe
  removal's new authorization lock in its concurrency test.
- Updated this document and `README.md`. No schema, dependency, BoardHub, or
  frontend component changes were needed for this step.

## Files changed in the frontend step

- Added `frontend/src/features/chat/ChatWorkspace.tsx`, `ChatPanel.tsx`, and
  `chat.css` for the toolbar/panel, shared UI composition, and responsive layout.
- Added `ChatController.ts`, `chatTransport.ts`, `chatApi.ts`, `chatState.ts`,
  and `types.ts` for board-scoped client state, SignalR lifecycle, HTTP commands,
  versioned local drafts, and typed contracts.
- Added `frontend/tests/chat-recovery.test.mjs` and `chat-transport.test.mjs`.
  Added `chat-browser-timers.test.mjs` for the retry-timer regression found by
  actual browser outage testing.
- Updated `frontend/src/features/boards/BoardWorkspace.tsx` only to mount the
  chat toolbar entry and overlay host; updated this document and `README.md`.
- No schema, dependency, auth, BoardHub, or canvas-controller changes in this step.

## Typing, moderation, and guest-cap verification

- 20 new backend cases passed: owner-only/private-resource access, owner mute
  protection, permanent/timed mute and unmute, readable history and idempotent
  message replay while muted, state persistence, validation, revision conflicts,
  slow-mode reset and owner exemption, membership-instance isolation, 100-row
  owner-list pagination, and rollback after domain/outbox SQL writes.
- Invitation tests submit 24 concurrent invitations and verify exactly 20 guests
  plus the owner. They also cover repeated invitations for one user, permission
  updates at capacity, lowered/zero limits without eviction, reloaded options,
  independent rate limits, and invitation/removal/send lock ordering.
- Typing tests use actual authenticated SignalR clients for authorization,
  renewal throttling, independent tab leases, stop/leave/disconnect/mute cleanup,
  private moderation references, stale subscription denial, delayed old-instance
  events, and the separate invocation limit. Registry expiry is clock-controlled.
- Ten new frontend cases passed: decimal typing sequences, duplicate/out-of-order
  delivery, expiry, bounded stop tombstones, tab aggregation, local throttle/idle
  cleanup, mute-state refresh, stale revision/instance rejection, coalesced state
  reads, transport scoping, a late send reply after a slow-mode change, and a
  stale mute error after unmute.
- Two isolated browser sessions verified live typing, slow-mode update/countdown,
  a one-hour mute, readable guest history while muted, live unmute, missing guest
  moderation controls, and HTTP 403 for a direct guest settings command. Mobile
  settings at 390×844 use the native modal, contain focus, dismiss with Escape,
  and have no horizontal overflow in light/dark themes. The sharing panel shows
  the configured guest count/limit. Both browser sessions reported no app errors.
  The disposable UI database used a cap of one; integration tests verify the
  default twenty. Test servers/browsers/database are cleaned up afterward.

The local SDK opts into Microsoft.Testing.Platform in `global.json`, while the
existing test project produces an xUnit executable. On this workstation the
repository's CI `dotnet test` command is rejected by that runner configuration;
verification used the built xUnit executable instead:

```sh
dotnet build tests/Wukna.IntegrationTests/Wukna.IntegrationTests.csproj --configuration Release --no-restore
dotnet tests/Wukna.IntegrationTests/bin/Release/net10.0/Wukna.IntegrationTests.dll -noLogo -noColor
```

This step does not alter the existing CI configuration.

## Files changed in the typing/moderation/guest-cap step

- Added `Features/Board/BoardOptions.cs`; updated `BoardEndpoints.cs`, `Program.cs`,
  and `appsettings.json` for validated options, membership write locks, guest-cap
  enforcement, the guest-limit read, and rate limiting.
- Added `Features/Chat/ChatModeration.cs`, `ChatRecipients.cs`, and `ChatTyping.cs`;
  updated `ChatAccess.cs`, `ChatHub.cs`, `ChatConnectionRegistry.cs`,
  `ChatRealtimePublisher.cs`, `ChatEndpoints.cs`, `ChatDtos.cs`, and
  `SendChatMessage.cs` for HTTP controls, state contracts, event handlers, typing,
  rate limiting, and settings revisions in send responses.
- Added `frontend/src/features/chat/ChatControls.tsx` and `chatTyping.ts`; updated
  `ChatController.ts`, `chatTransport.ts`, `chatApi.ts`, `types.ts`, `ChatPanel.tsx`,
  `ChatWorkspace.tsx`, and `chat.css`. Updated `frontend/src/api.ts` and
  `frontend/src/features/boards/components/BoardPanels.tsx` for the sharing hint
  and guest-limit errors.
- Added `tests/Wukna.IntegrationTests/ChatControlTestSupport.cs`,
  `ChatModerationTests.cs`, `ChatTypingTests.cs`, and `BoardGuestLimitTests.cs`;
  updated the existing `ChatRealtimeTests.cs` fault-publisher factory.
- Added `frontend/tests/chat-typing.test.mjs`; updated this document and `README.md`.
- No new migration, dependency, auth lifecycle, BoardHub, or canvas-controller changes.

## Private attachment backend

This checkpoint implements image attachments on the backend. The frontend now
includes a file picker, authenticated preview/download controls, and a handler for
attachment-state notifications. `Chat:Attachments:Enabled`
defaults to false. Production and Staging reject the development store and require
explicit OCI configuration. There is no public or container-local production fallback.

| Route | Contract |
| --- | --- |
| `POST /api/boards/{boardId}/chat/attachments` | Multipart `clientMessageId` (nonempty UUID), one `file`, optional plain-text `body` caption |
| `GET /api/boards/{boardId}/chat/attachments/{attachmentId}` | Authorized normalized WebP download, only when Available |
| Same GET with `?preview=true` | Authorized bounded WebP preview, only when Available |

Uploads return the same 201/200 send result as text sends. Their fingerprint
includes the original byte SHA-256, sanitized filename, expected MIME type, and
normalized caption. Identical completed retries replay the authoritative message;
changed content or reuse of a text operation ID returns 409. Concurrent retries
with a live reservation return `chat_upload_in_progress`/409 and `Retry-After: 2`;
retrying after completion reconciles the one message. Current membership is
required even for a replay. A completed replay does not acquire a new cooldown.

Images accept only JPEG (`.jpg`, `.jpeg`), PNG (`.png`), or WebP (`.webp`) with
matching declared MIME and decoded format. SVG, scripts, archives, and other
formats are rejected. The complete request, including chunked bodies, is bounded
to the input-file limit plus 64 KiB of multipart overhead. Filenames discard path
components, control characters, and bidi controls; remaining names are 1–255
characters. Captions are optional, at most 2,000 UTF-16 code units before trimming,
and normalize CRLF to LF. Sender identity comes from JWT, never form fields.

ImageSharp is reused from the existing avatar pipeline. Before decoding, dimensions
are limited to 4096×4096 and 16 million pixels. There are two simultaneous decoder
slots, bounded allocator buffers, and one decoded frame. Animated inputs produce
a static first frame. Images are auto-oriented, re-encoded as WebP, and stripped
of metadata; previews fit within 480×480. Encoding has its own output byte bounds.
The unchanged original and both derivatives remain private until all three pass
ClamAV. Decode/signature validation does not substitute for antivirus scanning.

### Configuration and development daemon

All values are under `Chat:Attachments` and can be supplied through ASP.NET Core
configuration (for example, `Chat__Attachments__MaxFileBytes`). Defaults:

| Option | Default |
| --- | --- |
| `Enabled` | false |
| `Provider` | `Local` (Development only); set `Oci` for private production storage |
| `Oci:Authentication` | `InstancePrincipals`; `ConfigFile` uses a mounted SDK config, absolute `Oci:ConfigFile` path, and `Oci:Profile` |
| `Oci:Region` / `Oci:Namespace` / `Oci:Bucket` / `Oci:Prefix` | Required when OCI is enabled; prefix defaults to `wukna-chat` |
| `WorkerEnabled` | true |
| `Directory` | `App_Data/chat-attachments` (development only) |
| `MaxFileBytes` | 5 MiB original |
| `MaxImageBytes` / `MaxPreviewBytes` | 8 MiB / 512 KiB |
| `BoardQuotaBytes` / `MemberQuotaBytes` | 512 MiB / 128 MiB per member within a board |
| `MaxPendingPerBoard` / `MaxPendingPerMember` | 40 / 5 |
| `UploadsPerMinute` | 5 per authenticated user across boards |
| `IoTimeoutSeconds` / `JobTimeoutSeconds` | 20 / 60 |
| `LeaseSeconds` / `ReservationSeconds` | 180 / 300 |
| `PollMilliseconds` / `BatchSize` | 1,000 / 5 |
| `ClamHost` / `ClamPort` / `ClamTimeoutSeconds` | `127.0.0.1` / 3310 / 20 |

Options validate bounds and require leases at least twice their relevant timeout.
The initial development override is `compose.attachments.dev.yaml`, used alongside
`compose.yaml`. It enables uploads only on the Development API, mounts a private
chat volume, and runs pinned `clamav/clamav:1.5.4` with a persistent signature volume
and a 4 GiB memory limit. The image runs clamd and freshclam continuously. It has
no published scanner port; the API reaches it on the Compose network. Keep signature
updates enabled and observe update/load failures. See the official
[Docker documentation](https://docs.clamav.net/manual/Installing/Docker.html) and
[1.5.4 release](https://github.com/Cisco-Talos/clamav/releases/tag/clamav-1.5.4).
This override does not modify or deploy the production Compose stack.

### Private OCI Object Storage

The OCI adapter uses Oracle's .NET SDK for signing, region selection, and credential
renewal. It acquires an immutable credential snapshot before each operation. A
blocked metadata refresh cannot start a late write after the caller's deadline;
one refresh is shared. Operations have the configured I/O timeout and no SDK
retries. PostgreSQL reservations, scan leases, and cleanup jobs remain the durable
retry authority.

Keys stay under `<prefix>/q/<UUID>` until all three blobs pass clamd. Promotion
uploads the bounded, checksum-verified derivatives to immutable
`<prefix>/a/<UUID>` names. Existing cleanup jobs then delete quarantine copies.
Repeated promotion verifies that an existing available object has the expected
bytes. Every upload sends SHA-256 and requires OCI to confirm it. Every download
checks length and SHA-256 before releasing its bounded seekable stream to the
scanner or authorized HTTP download. Missing checksums, changed bytes, oversized
or truncated responses, permission errors, and timeouts fail closed. Missing-object
deletes are idempotent; other failures stay in the durable cleanup queue. Chat
DTOs never contain OCI keys or public URLs.

Before every write or read, the adapter checks that the bucket is `NoPublicAccess`,
Standard, writable, has versioning disabled, and has no lifecycle policy,
preauthenticated links, or retention rules. Cleanup can still delete blobs if a
setting changes. Use a dedicated bucket and keep bucket administration rights
separate from the API identity; these checks cannot prevent an administrator from
changing settings between calls.

On OCI Compute, opt in with `compose.attachments.production.yaml` alongside
`compose.production.yaml` after creating a private bucket and instance-principal
dynamic group. Set `OCI_CHAT_REGION`, `OCI_CHAT_NAMESPACE`, `OCI_CHAT_BUCKET`, and
optionally `OCI_CHAT_PREFIX` in the protected deployment environment. The override
uses a pinned official Debian ClamAV 1.5.4 ARM64 image, persistent signatures,
and no published scanner port. The scanner has a separate egress network for
freshclam; the API reaches it on a private scanner network that excludes the DB.
Start only after the daemon health check succeeds. Default production Compose
does not enable uploads.
The production deployment helper includes this override only when
`/etc/wukna/attachments.enabled` is a root-owned mode `0600` regular file
containing `enabled`. Install the updated helper and a root-owned mode `0644`
`/home/ubuntu/wukna-deploy/compose.attachments.production.yaml`, add the OCI
variables to the protected environment, and create the marker last. A preflight
then validates the combined Compose configuration; deployment pulls and waits
for a healthy clamd before starting the API and frontend. Without the marker,
routine deployments continue using the base Compose file.
The [official ClamAV Docker repository](https://github.com/Cisco-Talos/clamav-docker)
documents Debian image support for ARM64.

Grant the API identity `BUCKET_READ` for one bucket and only `OBJECT_CREATE`,
`OBJECT_READ`, and `OBJECT_DELETE` for `<prefix>/*` in that bucket. This allows
privacy, PAR, retention, creation, read, and cleanup calls without bucket updates,
public links, object overwrite, or listing rights. An OCI IAM policy shape is:

```text
allow dynamic-group <chat-api-dynamic-group> to read buckets in compartment <chat-compartment> where target.bucket.name='<chat-bucket>'
allow dynamic-group <chat-api-dynamic-group> to manage objects in compartment <chat-compartment> where all {target.bucket.name='<chat-bucket>', target.object.name='<prefix>/*', any {request.permission='OBJECT_CREATE', request.permission='OBJECT_READ', request.permission='OBJECT_DELETE'}}
```

Check the policy against the real identity and compartment in a disposable bucket
before enabling uploads. Oracle documents these
[object permissions and bucket read calls](https://docs.oracle.com/en-us/iaas/Content/Identity/Reference/objectstoragepolicyreference.htm)
and [object-prefix policy patterns](https://docs.oracle.com/en-us/iaas/Content/Object/Tasks/managingobjects.htm).
Replace policy placeholders with the dedicated deployment values.
On non-Compute hosts, choose `ConfigFile` and mount the SDK config and key read-only
outside the image. The path must be absolute and profile explicit. API-key rotation
requires an application restart; instance principals renew through the SDK.

On 2026-10-04, the production Compute instance principal successfully read the
dedicated `wukna-chat-attachments` bucket in `me-riyadh-1`. Its reported settings
were `NoPublicAccess`, Standard, writable, versioning disabled, and no lifecycle
policy. The same identity listed zero preauthenticated requests and zero retention
rules. A single-part upload under `wukna-chat/q/` succeeded and OCI returned the
expected SHA-256 checksum; reading it returned the exact test content. Deletion
succeeded and a subsequent HEAD returned 404 for the same key. The pinned
ARM64 ClamAV image started healthy with a 4 GiB limit on the production host;
`clamdscan --stream` completed a clean scan with zero detections and detected the
EICAR test file with one detection. Instance-metadata access from the future API
container, the Compose scanner network, and the full application upload/scan path
remain unverified.
Before rollout, verify application upload, real scan, authorized download,
revoked-member denial, and deletion cleanup on the real bucket.
Network timeouts may leave an object whose server-side PUT completes after
cancellation; reconcile the dedicated prefix with durable attachment and cleanup
records during rollout.

The scanner implements NUL-framed `INSTREAM`, big-endian chunk lengths, a terminating
zero-length chunk, bounded stream size/reply length, and cancellation deadlines.
Only `stream: OK` grants Clean. An infection produces Rejected; protocol errors,
disconnects, timeouts, and daemon outages produce ScanFailed and bounded exponential
retry delays (2–300 seconds). clamd TCP has no authentication or encryption: keep
it private, as required by the
[official protocol documentation](https://docs.clamav.net/manual/Usage/ClamdProtocol.html).
No per-upload process is spawned and no simulated scanner is registered in the app.

### Transactions, leases, and cleanup

The first transaction takes the existing board/settings/membership/state locks,
checks mute/cooldown and quotas/backlog, and commits an expiring upload reservation.
It reserves original bytes plus twice the maximum derivative bytes, covering
quarantine and promoted copies. Settings serialize competing quota reservations.
Rejected files stop charging attachment metadata but continue charging durable
cleanup work until their blobs are deleted. Abandoned reservations remain charged
until cleanup succeeds.

Decoding occurs outside database locks. The final upload transaction rechecks
membership, moderation, and cooldown, locks the still-owned reservation, writes
three private quarantine objects, and atomically commits the attachment/message,
send slot, and MessageCreated outbox record while removing the reservation.
Storage I/O is limited to 20 seconds by default. Keeping the board authorization
and reservation locked across those writes prevents removal/cleanup from overtaking
an active writer; it can briefly delay other commands on that board. Antivirus
scanning never blocks the upload response.

The single attachment worker claims due rows with `FOR UPDATE SKIP LOCKED`, a
random lease token, expiry, and incremented attempts. Scanning state and its outbox
reference commit together. Antivirus work runs outside locks. Finalization takes
board key-share before the attachment row lock, rechecks ownership/expiry, promotes
both derivatives, and commits Available/Rejected with an AttachmentChanged outbox
reference and durable cleanup. A stale worker cannot acknowledge another lease.
An expired Scanning row or due ScanFailed row can be claimed by a fresh process.

Storage identifiers are immutable generated keys. Quarantine originals use `q/`;
normalized images/previews use `a/` with quarantine copies at corresponding `q/`
keys. Local promotion copies privately and can overwrite an incomplete prior
promotion while metadata remains unavailable. Successful scanning queues deletion
of the original and quarantine derivatives; infected attachments queue all copies
for deletion. Byte accounting remains conservative after successful cleanup.
No paths or keys appear in HTTP DTOs or SignalR events.

Board deletion locks the board before reading attachment keys and enqueues cleanup
in the deletion transaction. This serializes it with upload/promotion and ensures
cleanup survives cascades. The durable reservation owns any interrupted upload,
including a partial file. Cleanup jobs use expiring claims, lock ownership across
bounded deletes, treat missing objects as already deleted, and retry failures.

Downloads query attachment→message→board and lock current membership for the bounded
transfer. Pending, Scanning, ScanFailed, and Rejected return 409; unrelated/removed
members and cross-board IDs return 404. Responses use `no-store`, `nosniff`, and
sanitized WebP names, with no static-files registration or signed/public URL.
The default 5 MiB upload plus overhead fits the existing Nginx `/api/` 6 MiB limit.
Raising upload limits also requires a reviewed proxy limit change. Live production
attachment/proxy verification remains pending because production uploads are disabled.

`ChatAttachmentChanged` contains only event ID/version, board ID, message ID, and
attachment ID. Publication rechecks the resource board and targets current
membership instances through the existing safe recipient path. A future client
handler must refetch the authorized message; reconnect must also refresh older
pending attachment cards rather than relying only on forward history.

### Verification in this checkpoint

- The default full backend run passed 159 cases, with the real-daemon case left
  explicit. That case was run separately and passed, for 160 verified backend
  cases total (36 new cases in this checkpoint).
- Focused attachment/scanner tests passed before the broader run. Coverage includes
  MIME spoofing, executable/SVG rejection, malformed and oversized/chunked input,
  dimension limits, cross-board IDs, current-member downloads, no storage keys in
  DTOs, unchanged board activity, idempotency, shared text/attachment send slots,
  quotas/backlog/rate limits, lease fencing, protocol framing/error/timeout bounds,
  scanner outages, restart recovery, failed writes/promotions, cleanup after cascade,
  mute/removal between reservation and writes, and deletion during promotion.
- Real `clamav/clamav:1.5.4` loaded signatures and freshclam updates. The explicit
  test verified a clean HTTP image upload/download, standard EICAR detection through
  the actual scanner, rejection of an EICAR original seeded into the private worker
  fixture, unavailable downloads during a real socket outage, and successful retry
  after the connection was restored. EICAR appended to a PNG is not the standard
  test-file form; the fixture uses the
  [standard EICAR data](https://www.eicar.org/download-anti-malware-testfile/).
  No actual malware was used. Test-only fake scanners cover deterministic failure
  cases; the application's scanner registration is the real clamd client.
- Backend Release build passed with zero warnings/errors. The unchanged frontend
  passed all 154 cases and its production build; existing SignalR annotation and
  bundle-size warnings remain. `git diff --check` passed.
- The merged development Compose configuration validates without resolving secret
  env files, and the documented daemon health check returned `PONG`. Disposable
  PostgreSQL containers, private test-file directories, and the temporary scanner
  container were removed. No production deployment or migration was performed.

To repeat the real scan test, start a disposable official daemon with a loopback
binding, then run the explicit test using the repository's built xUnit executable:

```sh
docker run --detach --rm --name wukna-attachments-clam-check --publish 127.0.0.1:53310:3310 --memory 4g clamav/clamav:1.5.4
# Wait until clamd has loaded its signatures and clamdscan --ping=1 returns PONG.
dotnet tests/Wukna.IntegrationTests/bin/Release/net10.0/Wukna.IntegrationTests.dll -noLogo -noColor -explicit on -method Wukna.IntegrationTests.ChatAttachmentTests.Real_clamd_scans_originals_detects_eicar_and_recovers_after_socket_outage
docker stop wukna-attachments-clam-check
```

### Files changed in this checkpoint

- Added `Features/Chat/ChatAttachmentOptions.cs`, `ChatAttachmentStore.cs`,
  `ChatImageValidation.cs`, `UploadChatAttachment.cs`, `ClamAttachmentScanner.cs`,
  `ChatAttachmentJobs.cs`, and `DownloadChatAttachment.cs`.
- Added `Features/Chat/ChatSendPolicy.cs`; text and attachment sends share the same
  mute/cooldown checks and consumption. Updated `SendChatMessage.cs`,
  `ChatEndpoints.cs`, and `ChatRealtimePublisher.cs`.
- Updated `Features/Board/BoardEndpoints.cs` for transactional blob cleanup and
  deletion locking; updated `.gitignore`, `appsettings.json`, and `Dockerfile`.
  Added `compose.attachments.dev.yaml` (development only).
- Added `tests/Wukna.IntegrationTests/ChatAttachmentTests.cs`,
  `ChatAttachmentStorageTests.cs`, and `ClamAttachmentScannerTests.cs`.
  Updated this document and `README.md`.
- No migration, dependency, authentication, BoardHub, canvas, or frontend changes.

## Private OCI adapter checkpoint

The OCI step adds `Features/Chat/OciChatAttachmentClients.cs` and
`OciChatAttachmentStore.cs`, extends `ChatAttachmentOptions.cs` and
`ChatEndpoints.cs` for explicit provider selection, and adds
`compose.attachments.production.yaml`. The adapter plugs into the existing
upload, scan, download, and cleanup use cases without a schema change. The
production override is opt-in and has not been deployed. `README.md` and this
document describe the new state and operator requirements.

`OCI.DotNetSDK.Objectstorage` and Common 147.1.0 use Oracle's dual UPL/Apache
license and provide signed requests and credential handling. NuGet exposed an
older vulnerable transitive Newtonsoft.Json 12.0.3; a direct 13.0.4 reference
resolved that restore warning. The SDK's sample `NLog.config`, which would
enable debug request logs on disk, is excluded from the build output.

- The focused OCI and configuration run passed 23 cases. The final full backend
  run passed 174 cases with zero failures; the real-clamd case remained explicit
  and was not rerun in this checkpoint. Coverage includes signed SDK requests
  through a local HTTP fixture, private-bucket and link/retention rejection,
  immutable promotion after both 412 and 403, size and SHA-256 checks,
  cancellation, permission failure, and production option validation.
- The Release backend/test build passed with zero warnings or errors.
  `git diff --check` passed. The merged production Compose configuration
  validated with placeholder values and preserved the API's database and edge
  networks while isolating clamd on a private scanner network. Docker's image
  manifest confirmed an ARM64 variant for the pinned official Debian image.
- No frontend files or migration changed in this checkpoint. No live OCI
  credential, bucket, production host, or deployed scanner was exercised.

## Attachment frontend checkpoint

The board chat composer accepts a JPEG, PNG, or WebP image with an optional
2,000-character caption. It checks filename extension, MIME type, nonempty size,
and the server's 10 MiB configuration ceiling before sending; the backend remains
authoritative for its configured upload limit (5 MiB by default), decoded format,
image bounds, quota, moderation, and malware verdict. Uploads reuse the existing
optimistic operation ID and retry UI, so a lost HTTP reply can reconcile against
history or replay the same file without creating a second message. Files remain in
memory only; text drafts continue to use session storage.

Pending, scanning, delayed, rejected, and available statuses render on message
cards. Available previews load only as cards approach the viewport. Both previews
and downloads use `apiFetch` with the in-memory JWT and read a private blob into a
short-lived object URL; attachment URLs never contain access tokens. The browser
does not request image bytes before the server marks a scan Available. A
`ChatAttachmentChanged` event resolves the authorized message-by-ID endpoint and
replaces its row without advancing the message cursor. Rejoin and periodic refresh
also revisit bounded unresolved cards to recover missed events. Terminal scan
results are retained if an older history response completes afterward.

Frontend verification: focused recovery and transport tests, the complete frontend
test suite, TypeScript check, and production build. Browser layout and live
authenticated upload/download still require verification against a running board
with storage and ClamAV enabled.

## Scheduled-task domain and API checkpoint

`POST /api/boards/{boardId}/chat/scheduled-tasks` creates a structured chat
message; it never creates a board note or personal task. It accepts JSON
`clientMessageId`, `title`, optional `description`, offset-free `localStart`,
optional offset-free `localEnd`, `timeZoneId`, and optional
`startOffsetMinutes`/`endOffsetMinutes`. Example:

```json
{
  "clientMessageId": "b9e6d699-6f75-49c1-b106-84b1bc65c319",
  "title": "Deploy beta build",
  "description": "Review release notes",
  "localStart": "2026-10-09T14:00:00",
  "localEnd": "2026-10-09T15:30:00",
  "timeZoneId": "Asia/Beirut"
}
```

The server requires a current board membership, including for replays, and
applies the existing chat slow mode, mute, and per-user send rate limit. A
successful first request returns the ordinary `ChatSendResultDto` with 201,
and an identical retry returns 200 with the same message. A changed operation
or reuse across message types returns `chat_operation_conflict`/409. The
message, one-to-one `ScheduledChatTask`, cooldown, sequence, and outbox event
commit together. History and message-by-ID reads already project the task.

The server resolves IANA local times to authoritative UTC instants and stores
the authored zone and start offset. A daylight-saving gap returns
`chat_invalid_local_time`/400. An overlap requires an offset matching one of
that zone's valid offsets; otherwise it returns `chat_ambiguous_local_time` or
`chat_invalid_offset`/400. End times are resolved independently and must be
later than the start instant. This request shares the bounded JSON reader used
by text sends, including the 32 KiB bound for chunked bodies. No new migration
was needed because the scheduled-task entity and constraints were part of the
foundation migration.

The focused chat endpoint suite passed all 19 cases, including scheduled
creation by a read-only guest, replay and cross-type conflicts, slow mode,
mute, membership, and DST transition cases. The full backend suite reported
177 passed, zero failed, and one existing
real-clamd scenario was explicitly filtered. The Release backend/test build
completed with zero warnings and zero errors.

## Scheduled-task frontend checkpoint

The chat composer now offers an explicit **Create scheduled task** action. Its
separate form collects title, start, optional end and description, and an IANA
time zone. The form resolves local times against that zone before submission:
daylight-saving gaps are rejected, and repeated times require the author to
choose an occurrence with its UTC offset. It checks end ordering by UTC
instant. The server remains authoritative for time-zone and permission checks.

Posting uses the same pending, recovery, and idempotent retry path as other
chat sends. The form draft survives closing and reopening the panel while its
board workspace is mounted. A posted task appears as a compact card showing
the scheduled instant in the viewer's local zone plus the author's zone and
offset. The ordinary text and attachment drafts are separate.

Verification: five focused scheduling tests and all 165 frontend tests pass;
TypeScript checking and the production Vite build pass. The build reports
Rollup annotations in the existing SignalR dependency and its existing
large-chunk advisory. Authenticated browser layout checks remain to be done
against a running board.

## Calendar export checkpoint

`GET /api/boards/{boardId}/chat/messages/{messageId}/calendar.ics` returns a
single-event iCalendar file only for a current member of that board. The query
also scopes the message ID to the board and requires the scheduled-task type;
missing, wrong-board, and non-task messages return 404. A removed guest loses
export access immediately on the next request. Responses disable caching and
use an ID-derived download filename, `text/calendar; charset=utf-8`, and
`X-Content-Type-Options: nosniff`.

`ICalendarExportService` isolates Ical.Net 5.2.3 from the endpoint. Its VEVENT
uses a stable UID derived from the message GUID and a stable DTSTAMP from the
message creation time. DTSTART and optional DTEND use the persisted UTC
instants, so ambiguous local times cannot be reinterpreted on export. SUMMARY
and DESCRIPTION go through Ical.Net's serializer for escaping and line folding;
DESCRIPTION also carries the authored IANA zone and original offset.

The scheduled-task card offers **Add to calendar**. It retrieves the file with
the existing in-memory bearer-token API path, then downloads a short-lived blob
URL. The exported endpoint and serializer are covered by parsing, hostile text,
stable output, type/board isolation, and membership-removal tests.

Verification: the three focused calendar-export tests passed. The complete
backend integration suite passed 180 tests with one pre-existing real-clamd
scenario explicitly skipped. All 165 frontend tests, TypeScript checking, and
the production frontend build passed. A browser check of the download button
against a running authenticated board remains outstanding.

## Member read cursor checkpoint

Each existing `BoardMemberChatState` already has a `last_read_sequence` column;
this step starts using it without a migration or per-message receipt table.
`PUT /api/boards/{boardId}/chat/read` accepts the signed cursor of an actual
message and the current membership-instance ID. It rejects other-board,
invalid, and future positions and a stale instance after re-invitation. A
transaction advances the member's sequence only forward, so duplicate or
out-of-order updates are safe. The response returns the stored sequence and a
count of later messages from *other* members. Current membership is required
for every call; a removed member gets 404. Read writes have their own bounded
per-user rate policy.

Both the HTTP chat state and ChatHub join return `lastReadSequence` and
`unreadCount`. The frontend connects chat when a board opens, allowing the
collapsed button to show the cross-device count. The open panel starts near
the first unread row in its loaded window and acknowledges the latest message
only after the user reaches the bottom while the document is visible. If older
unread messages lie outside the loaded window, the badge remains until the
member reads further or explicitly chooses **Latest messages**. Acknowledgments
coalesce forward, retry uncertain responses, and ignore stale membership
instances. The existing cursor catch-up remains independent of read progress.

Verification: two focused read-cursor integration tests passed, including
ChatHub state, concurrent updates, authorization, and re-invitation. The full
backend suite passed 182 tests with one pre-existing real-clamd skip. All 168
frontend tests, TypeScript checking, and both production builds passed. Browser
verification of the initial unread scroll position and badge remains outstanding.

## Next steps

Production OCI/scanner verification and authenticated browser layout checks
follow as separate reviewable steps.
