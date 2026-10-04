# Notifications, reminders and browser push

The notification center now receives persistent chat activity, direct mentions, replies, personal-task reminders, reminders for saved scheduled chat tasks, board invitations, semantic shared-board activity, and task completion/reopening. SignalR uses the existing account connection. An open, audio-unlocked page can play custom clips in the foreground or in another tab/window. Closed or suspended pages rely on Web Push and browser/OS sound.

## Ownership and storage

`notifications` replaces `task_notifications`. Task reminders retain `Id = TaskId` and task-deletion cascades. Every query and mutation derives the recipient from the authenticated JWT. Board notifications carry the membership incarnation captured by the source transaction; removed or reinvited members cannot retrieve or receive an earlier incarnation. Push endpoint credentials are protected with the existing persistent Data Protection key ring, and endpoint hashes enforce unique bindings. Keep that key-ring volume between releases.

Unread is `readRevision < revision`, excluding dismissed entries. Chat unread is separate and based on the existing message sequence/read cursor. Reading through a chat sequence marks only notification aggregates covered by that cursor. Reading or dismissing a center entry does not mark chat messages read. Observed notification revisions prevent older clients marking newer aggregate activity read or dismissed.

`AddNotificationFoundation` preserves legacy reminder IDs, read and dismissal state before retiring the old table. `20261004004046_AddNotificationDelivery` adds chat mention/reply metadata, durable notification work, foreground leases, subscription storage, reminder generations and calendar reminder rows. Existing chat messages keep empty mentions/no reply. Existing personal reminders get fresh generation IDs. Downgrading delivery drops its new data; downgrading foundation retains only personal-task reminder notifications. Review the SQL and backup before a production migration; the API does not migrate on startup.

## Sources and delivery

Source work is committed in the transaction that creates the chat message, invitation, note mutation, or connection mutation. Offline board members are included. The actor is excluded. Note moves, sizes, colors, stacking, previews, presence, typing, camera changes, and unchanged semantic values produce no notification source. Note title/content edits, note creation/deletion, connection creation/deletion or changed endpoints, and actual completion/reopening do.

`NotificationDispatcher` claims up to 100 source rows with `FOR UPDATE SKIP LOCKED`, generates notifications and their delivery rows in one transaction, and processes a bounded delivery batch outside the source transaction. Dispatch uses 30-second leases, five-second delivery deadlines, exponential backoff with jitter, source/delivery expiry, and seven-day processed-work cleanup. Work IDs and unique recipient/source/kind keys make retries idempotent. Ordinary chat groups within fixed 30-second windows; semantic board activity groups within 60-second windows. Mentions, replies, and invitations retain separate entries. Aggregation updates have a serialized recipient/key lock and increasing revision. Delivery retries recheck current membership, preferences, revision, dismissal and reminder generation.

Chat generation has a two-second grace period for the existing visible-chat read acknowledgement. Already read messages generate no redundant center notification. Read acknowledgement requires a visible, focused window at the latest chat view; an unfocused window does not read new messages or pin the scrollback. Focus/blur and tab visibility changes update chat attention immediately. Foreground clients also suppress disruptive alerts for the same visible chat at its latest window. Pages reconcile the center quietly through REST on focus/reconnect/polling; missed history does not replay sounds. Sidebar board badges show chat unread, even when chat notification mode is muted. The global navigation badge shows notification unread.

Account settings persist global sound mute, volume, five sound switches, six category switches, in-app alert and push choices, and private preview opt-in. Each board has All activity, Mentions & replies, Muted, temporary mute, and sound mute controls. These are independent of the owner's moderation mute. Temporary mute expires without changing the underlying mode. Disabling a category suppresses new generation/alerts; sound mute preserves visual notifications and unread counts.

## Mentions and replies

The text composer provides a current-board member picker. A selected mention stores `{ userId, start, length }` offsets in the normalized body; editing a token clears its ping. Plain typed `@text` is not an implicit mention. The server validates visible token text, bounded/non-overlapping offsets and current board membership. Reply references must point to an accessible message on the same board. Replies start with Notify author enabled, and the sender can turn it off. Retry payload fingerprints include metadata while retaining the legacy fingerprint for ordinary text. A recipient who is both mentioned and replied to receives one priority notification. No roles, `@everyone` or `@here` were added.

This follows the user-facing behavior in [Discord Replies FAQ](https://support.discord.com/hc/en-us/articles/360057382374-Replies-FAQ) and [Discord notification settings](https://support.discord.com/hc/en-us/articles/215253258-Notifications-Settings-101). Wukna validates selected mentions rather than interpreting arbitrary strings as recipients.

## Scheduled tasks and reminders

The existing chat card still lasts 24 hours. Add to Wukna Calendar reuses its idempotent authenticated snapshot import, preserves title/description/time/zone/source, and creates no extra chat or board task. Download .ics continues through the original authorized export endpoint.

A saved imported calendar event has GET/PUT/DELETE `/api/calendar/events/{id}/reminder` and POST `/reminder/snooze`. Offsets range from 0 to 10,080 minutes and initial reminders must be future-due. The existing reminder worker claims personal and calendar reminder rows. The imported personal snapshot owns its reminder independently of chat expiry or later board membership. Editing its time rearms the reminder; deleting the event or cancelling removes its notifications. Personal task completion, cancellation, rescheduling and snoozing use the same generation mechanism. Task/calendar row locks serialize worker claims with changes, and delivery rejects obsolete generations. Upcoming reminders include both kinds; the compatibility field `taskId` holds the resource ID with `resourceKind` indicating `task` or `calendarEvent`.

## Sound mapping

Original supplied files remain under `frontend/public/sounds`, loaded only on playback:

| Preference | Clip |
| --- | --- |
| `chatSoundEnabled` | `mixkit-positive-notification-for-chat-messages.wav` |
| `taskReminderSoundEnabled` | `notification-for-task-reminder.wav` |
| `scheduledTaskPostedSoundEnabled` | `notification-when-calendar-task-sent-in-chat.wav` |
| `boardInvitationSoundEnabled` | `notification-when-invited-to-board.wav` |
| `taskCompletedSoundEnabled` | `notification-when-tasks-are-finished.wav` |

Posting a scheduled task uses its posting clip; a due reminder uses the reminder clip. Reopening and ordinary semantic edits have no completion sound. IndexedDB claims serialize page audio/alert consumption across tabs and the service worker. An open page attempts playback even when hidden or unfocused, provided its audio context was unlocked through user interaction and is still running. Only a visible, focused page shows an in-app toast, and reading the same chat at the latest view stays quiet. Background pages with muted or blocked audio leave the claim available for Web Push. Global/category/board sound mute and zero volume are checked. Incoming alerts and Test sound share one Web Audio player, resumed during trusted taps/key presses; clips load on first playback and reuse decoded buffers. Routine reconciliation, blur and tab hiding let the clip finish. Mute changes, read/dismiss events, and session cleanup cancel active or pending playback. A blocked context offers Enable sound; asset failures show a separate retry message. Test sound remains an explicit user action. Browsers may suspend background pages or audio; no custom playback is guaranteed from a closed or suspended page.

## Browser push

Account → Preferences & security has explicit browser opt-in, persistent delivery/previews switches, and device removal. Permission is never requested on page load. Up to eight current installations per account are allowed. An endpoint cannot be reassigned to another account/installation; enabling a browser refreshes its subscription keys. Logout clears the local binding, unsubscribes and deactivates that installation. Logout everywhere deactivates all account subscriptions on the server. Session expiry clears local binding and unsubscribes. No access token or refresh cookie is stored in the service worker or notification state database.

Authenticated `/api/notifications/push` APIs expose configuration (public key only), subscriptions (IDs/creation time only), registration, removal, installation disable, and foreground presence. Registration accepts only HTTPS default-port endpoints from Google FCM, Mozilla Push or Apple Push, with valid P-256/auth keys. Redirects and proxies are disabled; connections resolve and pin a public address. No endpoint, auth secret, private key, provider body or JWT is written to application delivery logs.

`Lib.Net.Http.WebPush` 3.3.1 supplies VAPID signing and encrypted delivery. Active visible, focused installation leases suppress push queuing and sending. Blur and tab hiding release that lease; the presence request runs alongside reconciliation rather than waiting for count/preference requests. Default payloads contain only generic Wukna activity, opaque routing IDs, revision, account/installation binding and sound policy; private activity titles require opt-in. Push expires after five minutes; stale revisions and read/dismissed notifications are suppressed. Provider 404/410 and permanent 4xx deactivate subscriptions, while temporary failures retry through durable work.

The dedicated notification service worker caches no pages or API data. It checks account/installation binding, hides previous-account previews, uses stable replacement tags and opens allowlisted internal resource routes. API/UI access checks remain authoritative after navigation. The web manifest enables Home Screen standalone behavior. iOS/iPadOS push requires opening the installed Home Screen app; browser/OS support and sound behavior differ.

Every received push shows an OS notification. It is not silently discarded when a tab becomes foreground: [WebKit requires a visible notification](https://webkit.org/blog/16535/meet-declarative-web-push/). Server leases prevent the normal foreground duplicate; an in-flight focus race can still produce a tagged, silent OS notification. If a page claimed delivery first, the push is silent and tells pages to let the custom clip finish. If push claimed first, the page remains quiet and browser/OS sound applies. The worker informs pages to clear a matching foreground alert. A repeated delivery replaces the same OS tag without renotifying. Exact once-only OS display or sound across a provider crash/focus race cannot be guaranteed.

## Operator configuration and release

Publishing, production migration and rollout remain with the user. No production state has been modified. Browser push is off by default; in-app delivery works without VAPID. No additional Oracle Object Storage permission is required for notifications. The API needs outbound HTTPS to the supported push providers.

Compose accepts `WEB_PUSH_ENABLED`, `WEB_PUSH_PUBLIC_KEY`, `WEB_PUSH_PRIVATE_KEY`, and `WEB_PUSH_SUBJECT`. Keep one stable P-256 key pair and a contact subject such as `mailto:your-address`; configure private values only in the root-protected production environment file. Invalid enabled configuration fails startup. Rotating keys requires re-enrolling browsers.

For local `dotnet watch` development, use a separate development key pair. The ignored `.env.web-push.local` file stores the generated pair with mode 0600. Configure `WebPush:Enabled`, `WebPush:PublicKey`, `WebPush:PrivateKey`, and `WebPush:Subject` through the project's .NET user-secrets store (using piped JSON keeps key values out of command arguments), then restart the development API. Local Docker uses the equivalent `WebPush__...` names in the ignored `.env.api`. Keep development keys separate from the VM's production keys.

In the receiving browser profile, refresh Wukna and open Account → Preferences & security → Browser notifications. Enable this browser, allow the permission prompt, then turn on Browser notifications under delivery settings and save. Click Test sound once in the receiving tab to verify/unlock its audio, leave that tab open in the background, and send a new message from another board member to test the custom clip. Close the receiving tab and send another message to test browser/OS Web Push sound. Desktop Brave may also require its Use Google services for push messaging preference; see [Brave's privacy settings](https://support.brave.app/hc/en-us/articles/360017989132-How-do-I-change-my-Privacy-Settings). Closed/suspended-page sound follows OS/browser settings.

Browser enrollment reports permission, site storage, service worker and push service failures separately from API failures. If Brave cannot connect to its push service, enable Use Google services for push messaging under Settings → Privacy and security, restart Brave, then retry enrollment. A genuine API error retains the normal authenticated API error message.

The new API image supports a one-time key-file command, which neither starts the API nor prints keys:

```sh
sudo docker run --rm --user 0 --network none \
  -v /etc/wukna:/keys \
  ghcr.io/ahmadkawkab-dev/wukna-api:sha-REVIEWED_IMAGE_SHA \
  --generate-vapid-keys /keys/web-push.env
```

It creates a mode-0600 file and refuses to overwrite an existing file. Once the new image is published, configure the stable values and subject together using the existing protected `/etc/wukna/production.env` workflow. Do not paste the private key into chat. Apply reviewed migrations before starting new workers/API, using the repository's production migration gate and database backup procedure. The release must retain the Data Protection key-ring volume. The worker script is served with `no-cache` to support updates.

## Verification and remaining external checks

`NotificationFoundationTests` covers preferences/revisions/ownership, membership incarnation, read/dismiss state, worker category policy and legacy migration preservation. `NotificationDeliveryTests` covers validated mentions/replies/retries, per-board mode filtering, concurrent generation, read-chat suppression, reinvitation, aggregation, independent saved-calendar reminders, cancellation/snooze and semantic-versus-geometry sources. `WebPushTests` covers provider/key validation, endpoint ownership, device caps, protected storage, encrypted VAPID delivery, foreground suppression, idempotent fanout, retryable errors and stale-subscription cleanup. Existing auth/chat/ICS/import/task/calendar/board/realtime tests provide regression coverage. Frontend tests cover API transport, mention editing, sound mapping and worker binding/tag/safe-click behavior.

Desktop browser verification was attempted again, but the browser runtime fails during connection setup. Thus actual permission dialogs, autoplay, responsive interaction and installed iOS push remain unverified here. Before release, manually check a visible same chat, another board, muted mode, two tabs, a background push, a reminder after 24-hour source expiry, logout/account switch, safe missing-resource navigation, and an iPhone/iPad Home Screen installation. Production verification waits for user-controlled publishing/configuration/migration and cannot be claimed as complete locally.
