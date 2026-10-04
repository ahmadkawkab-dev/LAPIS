# Wukna onboarding

The authenticated workspace offers a five-step interactive tour after session restoration,
the board list and the current page have finished loading. Stable `data-tour` hooks anchor
coach marks to Home, Boards, Calendar, Quick tasks and Templates in primary navigation.
Each step briefly explains the area; Boards includes notes, real-time collaboration and
board chat. On mobile, Boards, Quick tasks and Templates point to More with a short menu
hint. The real controls remain interactive. Advancing the tour never changes the current
page or opens a board. There are no editing exercises or “Try the highlighted control”
actions, and the tour never creates workspace data.

The Help control beside the desktop/mobile account area replays the tour. Back, Next,
Skip tour and Finish are keyboard accessible. Escape skips unless a native dialog is open.
Coach marks pause while dialogs or page loading UI are present, and show a dismissible
fallback if a target is unavailable. Closing restores focus to the previous control, the
visible replay button, or main content. No focus trap or pointer listener is installed.

## User preferences

`GET /api/profile/onboarding` returns `{ status, version }` for the JWT user.
`PUT /api/profile/onboarding` accepts `Completed` or `Skipped` and current version `1`.
The endpoint changes only those two user preferences; there is no client ownership field.
Both responses use `Cache-Control: no-store`. Repeated writes are idempotent. Older
clients cannot overwrite a higher saved tour version.

Migration `20261004180751_AddUserOnboarding` adds `onboarding_status` and
`onboarding_version` to `asp_net_users`, defaulting to `NotStarted` / `0`, with a check
constraint. This offers the tour to existing users too. The outer-tour refactor keeps
version `1`, so completed/skipped preferences remain effective without another migration.
Apply the reviewed migration before running the updated API. No new production settings
or secrets are needed. Use the normal migration deployment/backup procedure for production.

Completed/skipped users do not automatically see this version again, including on another
browser. The current step is transient: refreshing an unfinished tour restarts the short
tour from step one. Replay does not reset persisted state. Skip/Finish close immediately;
a failed save exposes a Retry saving control. If the user reloads before a failed save is
retried successfully, the server's previous state remains authoritative.

For a substantial revision, increase `TOUR_VERSION` and the endpoint's accepted current
version together. Users with older saved versions will receive the revised tour; an old
client will not offer an obsolete tour over a newer saved version.

## Rendering and measurement

Tour domain state and definitions live in `features/onboarding/tour.ts`. The authenticated
provider supplies a stable controller, and lazy-loads `WuknaTour` separately from the
workspace. Only the coach mark subscribes to progress/geometry changes. Positioning tries
right, left, bottom and top with viewport clamping. On narrow screens it uses a compact
edge-positioned card that avoids the highlighted bottom navigation. Scroll, resize,
target/card ResizeObserver and filtered DOM mutations reposition the card without polling,
watching pointer movement, or changing canvas pan/zoom. Themes, focus, spacing and motion
use Wukna's existing tokens. The compact footer groups Skip, Back and Next/Finish;
position changes ease between navigation links, with reduced motion disabling animation.

Local `wukna:tour-event` CustomEvents emit `tour_started`, `tour_step_viewed`,
`tour_step_completed`, `tour_skipped`, `tour_completed` and `tour_replayed`, carrying only
tour version and step. No analytics platform, personal data collection or network delivery
is introduced.

## Asset credit and verification

The locally bundled Help icon is [Question mark circle by QudaDesign on Flaticon](https://www.flaticon.com/free-icon/question-mark-circle_10380844).
The original PNG is unchanged and used as a CSS mask so its color follows the theme.
Visible attribution is in the account footer; the source and license links accompany the
asset in `public/icons/flaticon-help.LICENSE.txt`. No Flaticon icon library or external
runtime request is needed.

Focused frontend tests cover state/versions, outcomes/retry/replay, API payload and
cancellation, the five outer steps, desktop/mobile navigation and missing targets,
real React controls, focus and Escape,
dialog/loading suspension, and positioning at 375/768/1024/1440px. JSDOM is a test-only
dependency; geometry is supplied by the tests and does not substitute for visual browser
review. Backend integration tests use isolated PostgreSQL databases to cover authorization,
cross-session persistence, idempotency, input validation, future-version protection and
preservation of unrelated user/workspace state.
