# Frontend audit and refactor — October 4, 2026

Written before implementation. Repository source is authoritative. The previous audit incorrectly describes Chat as unavailable and omits Home, Templates and expanded notification workflows. Those features are implemented. Journal, Library and Pictures remain development previews or coming-soon routes.

## Stack and boundaries

React 19.0, TypeScript 5.7, Vite 6, Tailwind 3.4, Lucide, SignalR. Custom native-dialog controls; no shadcn/Radix, routing, animation or query library. Preserve REST DTOs, in-memory tokens, refresh cookies, mutation queues, concurrency versions, camera restoration, finite world geometry, realtime events, attachment scanning, time-zone semantics and persisted reminders. No backend or dependency migration needed.

## Inventory

| Surface | Components reviewed | Disposition |
| --- | --- | --- |
| Public | PublicHome, PublicFrame, PrivacyPolicyPage, TermsOfServicePage | Keep legal content and links; add skip navigation. |
| Authentication | AuthScreen; bootstrap/callback in App | Keep autofill, paste, OAuth and recovery; password disclosure and field errors. |
| Navigation | AppShell, Sidebar, MobileHeader, MobileNav | Home, Boards, Week, Calendar, Quick tasks, Templates, Notifications and Account are current destinations; route focus, page titles, collapsed search/badges. |
| Home | DashboardPage | Keep bounded reads; content-shaped skeletons, empty CTAs, long titles. |
| Boards | BoardLanding, BoardCard, BoardGrid, BoardPreview, BoardActionsDialog | Keep previews, roles, counts, links, actions/cap; create feedback and inline-form focus. |
| Canvas | BoardWorkspace, NoteCard, InlineNoteText, PropertiesEditor, InspectorFrame, ConnectionLayer, ZoomControls | Keep geometry, gestures, nudges, camera and mutations; pointer move controls supplement size/connection forms. |
| Collaboration | BoardPresence, RemoteCursors, NoteEditingIndicator, CollaborationAnnouncements, RealtimeHealth, SharePanel, MemberActionsMenu | Keep identity/live status/permissions; bound menus, label connection selector, sharing feedback. |
| Board checklists | NoteCard items, TasksPanel | Keep drafts, completion/removal and distinction from personal tasks. |
| Chat | ChatWorkspace, ChatPanel, Message, ChatControls | Keep controller, bounded history, anchoring, unread/moderation; history skeleton, metadata, IME/mentions, composer reflow. |
| Chat resources | ChatAttachmentView, ChatScheduledTaskCard, ChatScheduledTaskComposer | Keep authorized downloads, scanning, DST, import/ICS; inline busy feedback. |
| Tasks | TasksPage, TaskQuickAdd, TaskDetails, TaskReminderControl, QuickTemplates, WeeklyPlanner, DayTaskBoard, InlineTaskAdd, TaskRow | Forms already offer drag alternatives; page loading, details focus/Escape/restoration, long text. |
| Planning | TaskPlanningPanel, TemplatesPage, TemplatesDialog | Keep lists/zone/apply merge/bulk protection; busy states and responsive cards. |
| Calendar | CalendarPage, CalendarEventEditor, CalendarTaskTray, TaskScheduleDialog, CalendarReminderPanel | Keep Month/Agenda/tray/moves/all-day semantics; separate current/selected date, readable targets and skeleton. |
| Notifications | NotificationsPage, NotificationRuntime, NotificationPreferences, BoardNotificationPreferences, BrowserPushPreferences | Keep revisions/delivery/sound/push/counts; unread text, activity-aware empty copy, checking/retry. |
| Account | AccountPanel, ThemeControl | Keep profile/avatar/Google/session; field errors, read-only email, responsive sizing. |
| Future | FuturePreviewApp, PreviewUI, LibraryPage, PicturesPage, JournalPage | Keep sample labeling/production boundary; normalized controls/themes. |
| Shared | Button, IconButton, Field, Dialog, Notice, Avatar, AppErrorBoundary | Existing primitives are sound starting points; busy geometry, required hints, modal focus/close, toast dwell. |
| Foundation | tokens, fonts, foundation, shell, boards, board-foundation, presence, feature CSS, Tailwind | Keep local fonts/4–8 rhythm/pigments; consolidate palettes and undersized controls/type. |

## Priorities — systemic

| Priority / where | Problem / impact | Skill rule | Proposed correction | Scope / risk |
| --- | --- | --- | --- | --- |
| P0 Dialog | First-button focus overrides autofocus and may focus destruction; hidden descendants counted; no standard close. | focus-management; escape-routes; React focus guidance | Focus explicit autofocus or heading; native containment/restoration, visible close/busy guard. | Verify Tab/Escape/nested dialogs. |
| P0 Shell | Skip target not focusable; transitions leave focus in old nav. | skip-links; focus-on-route-change | Focusable main; route focus/title without resetting canvas on same-path updates. | Browser back and dialogs. |
| P0/P1 Tokens | Shell palette differs from body portals; light control border below 3:1; captions 9–11px. | color-contrast; color-semantic; readable-font-size | Paired root palette; body 16px, controls 14px, metadata ≥12px, contrast-checked border. | Foundation; no world scaling. |
| P1 Button | No shared busy/pressed state; changing labels change dimensions. | loading-buttons; submit-feedback; state-clarity | Busy prop disables, preserves original label geometry, announces current label; shared sizes. | Preserve existing variants/callers. |
| P1 Forms | Required/error hints inconsistent; registration only mentions length. | form-labels; error-placement; accessible-authentication | Required hint, associated announced error, password disclosure/backend requirements. | Keep native validation/auth. |
| P2 Layout | Feature overrides reduce targets/type; mobile bars obscure focus. | web-target-size; touch-density; focus-not-obscured | Feature normalization, safe scroll padding, touch targets/16px mobile inputs. | Explicit dense-calendar exceptions. |
| P3 Loading | Home/Tasks/Calendar/Notifications/Templates/Chat begin with text/blank content. | progressive-loading; content-jumping; loading-states | Delayed shaped placeholders; retain content during refresh. | No request/race changes. |
| P4 Navigation | Collapsed shortcut targets hidden search; badges disappear with labels. | keyboard-nav; nav-state-active | Expand/focus; keep badge; mark secondary More active. | Preserve deep links. |
| P5 Overflow | Tiny metadata/narrow actions/long headings. | font-scale; long-token-wrapping | Type roles, shrinkable children, wrapping/title disclosure. | No blanket break-all. |
| P6 Motion/performance | Animated grid width; eager feature imports. | transform-performance; bundle-splitting; reduced-motion | Remove width animation; native lazy routes/stable fallback. | Realtime runtime stays mounted. |

## Priorities — page-specific

| Priority / where | Problem / impact | Skill rule | Proposed correction | Scope / risk |
| --- | --- | --- | --- | --- |
| P0 Board properties | Keyboard nudges, no pointer move alternative. | dragging-alternative | Direction controls through bounded visual mutation path. | No transport/queue changes. |
| P0 Task details | No opening focus/Escape/restoration. | keyboard-nav; focus-management | Focus field, Escape, restore origin. | Nonmodal desktop/mobile panel. |
| P0 Chat mentions | Multiline textarea assigned combobox; result index can become stale. | voiceover-sr; keyboard-nav | Native textbox and guarded suggestion selection. | Preserve IME/controller. |
| P1 Auth/profile | Stable field errors only global; disabled email not read-only. | error-placement; read-only-distinction | Map verified error codes near fields. | Retain drafts/generic errors. |
| P2 Calendar | Selected date uses current-date semantics; tiny cells/type. | color-not-only; web-target-size | Separate Today/selection; compact readable cells/mobile day list. | Keep bounded ranges/timezone. |
| P3 Push/chat settings | Unavailable shown during checking; preference error lacks retry. | loading-states; error-recovery | Checking/retry and safe busy controls. | No automatic permission prompt. |
| P3 Notifications | Unread largely color; empty copy only task reminders. | color-not-only; empty-states | Unread text, activity-aware copy/preferences link. | Revision-aware actions preserved. |
| P3 Board creation | No focus restore/field association. | focus-management; error-placement | Restore trigger, protect busy dismissal, associate error. | Preserve cap warning. |
| P5 Chat | 11px timestamps and cramped controls. | font-scale; touch-density | Readable metadata, narrow footer wrap. | Preserve scroll/draft. |

## Skill evidence and direction

Requested skill obtained at `/tmp/wukna-ui-ux-pro-max`, not vendored. Read `.claude/skills/ui-ux-pro-max/SKILL.md` and `references/quick-reference.md`. [Source](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill/tree/main/.claude/skills/ui-ux-pro-max).

Executed `scripts/search.py`:

- `collaborative productivity personal workspace calm modern --design-system -p Wukna`: Flat Design, low visual cost, focus/contrast/restrained effects. Retain Wukna green and local Instrument Sans/Newsreader; generated teal/orange/Jakarta and marketing demo pattern are unsuitable replacements for established app identity.
- UX queries: `keyboard focus modal`, `loading feedback async buttons`, `dragging movements`, `error summary validation`, `responsive app sidebar`, `dark mode contrast`.
- `focus dialog forms --stack react`; `responsive overflow input --stack html-tailwind`.
- `code splitting lazy --domain react` returns a Next.js-specific example; use native React lazy/Suspense. Stack dataset versions are newer than this repo; no upgrades inferred.

P0 accessibility/interaction → P1 foundations → P2 layout → P3 states → P4 navigation → P5 polish → P6 motion. Apply existing 4/8 spacing, semantic surfaces, modest 6/10px radii, 20/24px app headings, one primary action, 36px desktop/44px touch controls, 120/180ms motion, delayed skeletons and inline busy feedback. Tokens → primitives → features → pages. Update canonical design system with decisions.

## Verification boundary

Implementation/browser verification pending. Run actual scripts: `npm run typecheck`, `npm test`, `npm run build` (no lint script), `git diff --check`. Check 375/768/1024/1440px, themes, keyboard, text enlargement, reduced motion, long text, loading/empty/error. Record fixture UI checks separately from live backend/realtime flows. Token checks and automation alone cannot certify WCAG compliance.
