# Wukna UI/UX Design System

This document is Wukna's canonical product-interface reference. Read it before adding or changing frontend visual or interaction patterns. Update it in the same change whenever a system-level UI/UX decision is introduced or formalized.

Wukna's product expression is:

> Calm at rest. Clear when active. Quietly alive when people collaborate.

The interface should feel personal, spacious, crafted, creative, intelligent, warm, modern, responsive, trustworthy, and collaborative without becoming noisy. Nature is a structural metaphor: leaves are individual pieces, branches are relationships, and Wukna is the containing space. It is not a mandate to decorate every surface with plants.

## Decision order

When guidance conflicts, prioritize:

1. Correctness
2. Accessibility
3. User comprehension
4. Established Wukna interaction semantics
5. Consistency
6. Responsiveness
7. Performance
8. Visual refinement

UI work must not alter PostgreSQL authority, REST mutation semantics, `xmin` optimistic concurrency, mutation queues, realtime reconciliation, SignalR authorization, presence/editing leases, geometry expiry, cursor expiry, or access revocation behavior unless a separate correctness issue is documented first.

## Core principles

### Hierarchy before decoration

Every major surface must make these questions easy to answer:

1. Where am I?
2. What am I working on?
3. What is the primary action?
4. What are collaborators doing?
5. What is secondary?
6. What requires attention?

Use scale, spacing, contrast, position, typography, and grouping before adding color. Primary actions, collaboration awareness, metadata, destructive actions, and navigation must not have equal visual weight.

### Whitespace is functional

Whitespace separates unrelated groups, associates related controls, establishes reading rhythm, and keeps canvas chrome from competing with content. Do not fill open areas with redundant cards, labels, controls, decorative leaves, or background patterns.

### Still until touched

Motion communicates cause, relationship, transition, or state. Wukna does not use ambient leaf movement, continuous pulses, aggressive springs, cursor trails, particles, unnecessary parallax, or decorative animation.

### Recognition over recall

Common actions stay visible or immediately reachable. Rare actions may use inspectors, contextual menus, or secondary sheets. Tooltips supplement accessible labels; they never replace them.

### Primary navigation

The signed-in shell's primary destinations are Boards, Tasks, and Calendar. Account is reached through the persistent identity control: the desktop sidebar account row or mobile header avatar. Upcoming Library, Pictures, and Journal routes remain valid for direct links and development previews but do not compete with working destinations in primary navigation. A destination only joins primary navigation when it supports a meaningful workflow.

The October 4 implementation and browser evidence are recorded in [the refactor audit](UI_UX_REFACTOR_AUDIT.md).

## Semantic tokens

Tokens live in `frontend/src/styles/tokens.css`. Components use semantic tokens instead of hard-coded product colors or arbitrary stacking values.

The remaining legacy board geometry rules in `frontend/src/styles.css` also reference these tokens. Palette values belong in `tokens.css`; note pigments, contrast-checked note ink, and collaborator identity are the deliberate content/identity exceptions. As board geometry migrates, remove superseded legacy rules instead of introducing a second palette.

Root tokens own both light and dark palettes, including native-dialog portals outside the shell. Keep selected foreground/surfaces, control borders and status pairs together. Do not add a competing shell palette. The semantic contrast regression test checks normal text at 4.5:1 and control/focus boundaries at 3:1 against their intended surfaces.

### Surfaces and hierarchy

- `--background`: application background
- `--canvas`: board/canvas and recessed input surface
- `--surface`: cards, panels, popovers, and elevated controls
- `--foreground`: primary text and icons
- `--muted-foreground`: secondary text and metadata
- `--primary` / `--primary-foreground`: primary action and brand emphasis
- `--secondary`: quiet controls and grouped states
- `--selected`: selected navigation and low-emphasis active state
- `--border`: structural separation
- `--control-border`: interactive control boundary
- `--focus`: keyboard focus
- `--hover`: quiet hover state

### Status

- `--success`: completed or restored state
- `--warning`: attention without destructive failure
- `--destructive`: destructive actions and errors
- `--destructive-on-light-content` / `--destructive-on-dark-content`: contrast-checked destructive ink candidates for user-controlled note pigments
- `--status-info`: connection or neutral system information
- `--status-*-surface`: low-emphasis status backgrounds

Status never depends on color alone. Pair color with clear text and, where useful, an icon.

### Collaboration

The six curated `--collaborator-*` colors are the only collaboration identity palette. Each color has a paired foreground token. A deterministic hash of `userId` selects the palette position through `collaboratorIdentity.ts`.

The same person must use the same identity color across:

- presence
- editing indicators
- remote geometry
- live cursors
- future collaborative selections
- avatar accents

Identity also includes a name, initials, avatar, or accessible label. Color is never the only identifier.

### Notes

Persisted notes use the Wukna pigment set:

- Paper
- Moss
- Clay
- Seed
- Sky
- Berry

Opaque custom colors use a computed foreground checked against the actual Wukna ink colors, with a black or white fallback when the preferred inks miss 4.5:1 contrast. Note editors use the note's own background and foreground rather than the app surface, and native controls use the note's light or dark color scheme. New UI must not introduce arbitrary color pickers without a documented content need and contrast-safe foreground handling.

Checklist controls on a note use its computed ink for checkboxes and focus. Destructive note actions choose a contrast-checked red for that pigment; when neither red reaches 4.5:1, they use the note ink and retain an explicit destructive label. Shared board member lists scroll with their panel so the invite form cannot squeeze existing members out of view.

Meaningful note text uses the computed ink at full strength, including completed checklist labels. Opacity on a foreground that only just meets 4.5:1 would make user-selected pigments unreadable; completion remains visible through the checkbox and line-through.

Notes and task lists share content-aware resize bounds. Width stays between a readable minimum and 560px; the minimum expands for long titles or checklist labels. Height has a content floor, with task rows increasing the task-list minimum as they are added. Notes cap at 640px and task lists at 720px; content scrolls only after reaching its cap. The bottom-left resize target remains available without a clipped decorative arrow. Inline titles and checklist labels must remain clickable for editing on desktop, including when the card supports pointer dragging.

### Layers

Use the semantic layer tokens in increasing order:

1. canvas
2. connections
3. notes
4. remote geometry
5. selection
6. collaboration
7. toolbar
8. panel
9. popover
10. modal
11. toast

Do not introduce arbitrary `z-index` escalation.

## Typography

Primary UI: Instrument Sans.

Editorial accent, only where established and justified: Newsreader.

Metadata, only where compact technical values benefit: DM Mono.

Working scale:

- body and editors: 16px
- controls and navigation: 14–16px
- metadata and secondary labels: 12–14px
- app page headings: 24px; section headings: 18px; card titles: 16px
- public/editorial headings: responsive `clamp()` scale

Use regular, medium, and semibold weights. Avoid 10px and 11px product text. Body/editor line height is generally 1.4–1.6. Headings may use tighter leading. Do not justify body text. Long-form content should usually remain within about 65–75 characters per line.

## Spacing, radius, and elevation

Use the 4px spacing foundation: 4, 8, 12, 16, 20, 24, 32, 40, 48, and 64px.

Approximate radius roles:

- controls: 6px
- cards and notes: 10–12px
- panels: 10px; retain existing 12px note corners

Prefer borders, backgrounds, and spacing before shadows. Shadows are shallow and indicate elevation, not decoration. Avoid bubbly radii and large glow effects.

## Components

### Buttons

Use the shared `Button` and `IconButton` primitives.

- Primary: the limited dominant action on a view
- Secondary: bordered ordinary action
- Quiet: tertiary or low-emphasis action
- Danger: destructive action, separated from routine editing

Default and touch controls target 44px. Compact desktop controls use 36px; densely repeated calendar cells may use 24–32px with spacing. Icons remain 16–20px inside these targets. The shared `loading` state disables duplicate submission, hides the original content visually, and reserves the maximum of the original and optional `loadingLabel` width from the first render. Short controls use a spinner with a screen-reader label; do not render a long default loading label into a short Add button.

### Inputs

Inputs have persistent labels, tokenized borders and surfaces, 14px compact desktop text or 16px editor text, visible invalid state, and visible keyboard focus. Touch/narrow input text is at least 16px, including dense feature forms, to avoid browser zoom on focus. Placeholder text is supplemental, not a label. Required shared fields include a visible hint; associated errors are announced. Authentication supports paste/autofill and a password disclosure control. Read-only information stays selectable.

### Dialogs, panels, and sheets

An owned board's sidebar entry opens Board actions on right-click or Shift+F10. Each owned board card also has a visible Board actions button so touch users can reach the same choices. Rename uses the shared field and dialog controls. Delete requires a separate confirmation that names the board and explains that its notes, tasks, and connections are removed. Guests have no owner actions. After deletion, the board disappears from every member's list and active viewers leave it.

Routine notices remain for six seconds and restart their reading interval after hover/focus. Warnings persist until dismissed. Dismiss uses the brief exit animation; reduced motion removes it. Avoid stacking multiple notices in the same screen location.

Modal dialogs use the shared native-dialog primitive for focus containment, Escape behavior, inert background, accessible naming, and focus restoration. Initially focus explicit autofocus or the heading; reverse Tab from a heading wraps to the last control. Provide a visible close action and pass `busy` while submitting. On narrow screens Task details is a modal; desktop remains a dock. Route transitions focus the main region and reset page scroll without changing canvas coordinates or its stored camera.

Account dialog sections use constrained grid tracks and wrapping controls so email, sign-in methods, and session actions remain within the dialog at large text sizes. The dialog itself scrolls vertically when its content exceeds the viewport.

Desktop inspectors and secondary panels use grouped sections. Board Properties occupies a dedicated right-hand dock, approximately 280–340px and never more than about 45% of the workspace. Quick mobile properties use an in-flow bottom panel that reserves layout space instead of covering board objects. Full-height editing is reserved for an explicit focused-writing action; desktop sidebars are not merely squeezed narrower.

### Focus

Every interactive element has a persistent `:focus-visible` indicator with sufficient contrast on paper, pigments, selected states, and dark surfaces. Never apply `outline: none` or `outline: 0` without a verified replacement. Sticky headers, panels, sheets, and overlays must not obscure focused controls.

## Board and canvas language

The canvas is the focal surface. Toolbar, title, presence, inspector, navigation, and floating actions remain visually quiet. Colorful notes do not sit inside equally colorful chrome.

Notes prioritize content:

- comfortable internal padding
- clear title/body hierarchy
- readable line height and contrast
- accessible action targets
- restrained radius and shadow
- no decorative plant motifs inside ordinary notes

The corner resize target stays operable and keyboard focusable without a permanent clipped-corner mark. Its hover/focus state and accessible name communicate the action when used; numeric size fields remain available in Properties.

Connections use geometry, arrow direction, line style, and accessible descriptions so their meaning is not color-dependent.

Committed connection paths attach to the closest pair of cardinal note edges and adapt when notes move. The connection picker omits notes already connected in either direction; removing that relationship makes the target eligible again. Pointer handles must stop focus/click propagation so beginning a drag does not open the note inspector. A completed card drag suppresses its generated click before that click can activate inline title editing.

## Board editor state

Selection, text editing, and properties are independent states:

- selecting a note or task list highlights it without opening another surface;
- activating a title, body, or checklist label edits that field inline;
- activating Properties opens secondary appearance, dimensions, relationships, and destructive actions;
- simple selection never starts an editing indicator.

Ordinary text stays close to its spatial context. Note titles and bodies, task-list titles, and checklist-item text edit directly on their cards or rows. Properties do not duplicate those text fields.

### Draft ownership and recovery

UI presentation components never own the lifetime of unsaved board drafts. A dedicated editor-state subsystem owns per-note text drafts independently from authoritative board state and independently from desktop, tablet, or mobile presentation.

A dirty draft stores only the user, board and note scope implied by its storage key, the note ID, title, content, base `xmin` version, and update time. It never stores credentials, permissions, collaborators, or whole board collections. Drafts are written to `sessionStorage`, so they survive same-session reloads but do not become permanent documents.

The rendering model is:

```text
authoritative note + optional local text draft = visible editor value
```

Typing never mutates the authoritative note object. A draft is created only after a local text change and is removed when it again matches authoritative text. Successful REST saves clear a fully saved draft or rebase any remaining unsaved field onto the returned version. Failed saves preserve it.

On reload, a draft whose base version matches the authoritative note is restored normally. If a newer authoritative version exists, both values remain available and the draft enters recovery state. Realtime updates follow the same rule: clean editors accept authoritative updates normally; dirty editors retain local text and disclose that review is required. PostgreSQL and `xmin` remain the final durable concurrency authority.

Draft keys are scoped by user, board, and note. Access revocation and confirmed deletion remove affected drafts. Ordinary board navigation may leave them recoverable for the current browser session. Account isolation must prevent one user from ever seeing another user's drafts.

### Inspector and responsive presentation

Board editing surfaces must preserve spatial context. On desktop, the Properties/editor surface is docked on the right as part of the workspace layout and must never obscure the canvas. The canvas and dock are layout siblings separated by a quiet semantic border; the dock is not absolutely or fixed positioned and does not use floating-card styling.

On desktop and wide tablets, opening Properties reduces the canvas viewport while leaving every persisted note coordinate unchanged. The dock uses a responsive width of approximately 280–340px with a 45% workspace ceiling so the board retains a useful minimum area. Closing it expands the same viewport without deselecting the object or resetting the user's scroll position.

After the viewport changes, `revealBoardNode` performs the minimum required scroll adjustment only when the inspected object is clipped. It does not recenter an already visible object and never mutates note geometry. Pointer, connection-draft, remote-cursor, and geometry coordinates remain board-relative by incorporating the viewport scroll offset at the input boundary.

Narrow tablets and mobile place quick Properties in an in-flow bottom region. The canvas becomes shorter instead of remaining underneath the panel, preserving awareness of the selected object and nearby relationships. Every presentation reads the same editor state; changing breakpoints may remount a surface but must not destroy drafts. Board Properties and other secondary board panels are mutually exclusive so multiple panels cannot collapse the usable canvas.

At phone widths, the board header gives the back link and board title a full first row. Permission, collaboration status, and board actions flow below it instead of compressing or covering the title.

Phone note headers keep the title and Properties action visible as text grows. The decorative drag grip is omitted there; the header remains draggable, and note text scrolls within its persisted card size when the user's text scale exceeds that geometry.

Checklist rows keep their own height when a deletion confirmation expands. The checklist scroll area keeps both confirmation controls reachable without overlapping the next item action.

The dock hierarchy is intentionally progressive: object context and draft recovery, Appearance, Connections, Details, then a separated Danger zone. Title, note body, task-list title, checklist text, and completion stay inline on the board object; the dock does not duplicate routine text editing.

Opening Properties remembers its originating control. Closing with the close action or Escape restores focus to that control, then falls back to the selected note or board when the origin no longer exists. Switching directly to another board panel closes Properties without stealing focus back to the old origin. Escape exits inline editing or closes the current transient surface without deleting dirty text.

Properties offers four pointer direction controls through the bounded visual mutation queue in addition to note-keyboard nudges and numeric resize fields. Connections can be created and reconnected with card/side selectors using the same concurrency and conflict behavior as dragging.

## Keyboard and non-drag access

Expected behavior:

- Tab and Shift+Tab move through controls predictably
- Enter or Space activates focused controls
- Escape closes or cancels transient layers and modes
- focused notes can be selected without a pointer
- Arrow keys move a focused note in small increments
- Shift+Arrow moves it in larger increments
- width and height can be changed through numeric inspector controls
- connections can be created by selecting a note, choosing a target and relationship, and activating Add connection

Dragging remains available as the spatial canvas's efficient pointer interaction, but it is not the only path for moving, resizing, or connecting content.

## Collaboration states

Priority, highest first:

1. save conflict or other critical error
2. access revocation or permission loss
3. live collaboration unavailable
4. reconnecting or connecting
5. active editing or remote geometry
6. presence
7. passive identity

### Presence

Presence answers “Who is here?” It uses a compact avatar stack and a viewer list with accessible names. Infrastructure such as connection/tab count is not user-facing.

Presence is ephemeral active-viewer state. Membership is durable board authorization. The member list never derives from presence and retains offline collaborators.

### Editing

Editing answers “Who is actively editing this object?” It does not imply ownership or locking. Use an anchored, reserved, or overlay indicator that does not resize the note or obscure content.

### Remote geometry

Remote drag and resize are temporary previews. Use the collaborator identity color, a restrained outline, and a small identity marker. The preview must remain distinguishable from authoritative committed geometry without reducing note readability.

The current presentation uses a dashed collaborator-colored boundary plus initials and a short “moving” or “resizing” label. Preview packets remain unannounced because frame-by-frame assistive-technology updates would create noise; durable state and meaningful collaboration transitions remain authoritative.

### Cursors

Live cursors use collaborator color, a simple pointer, and a short name label. Movement briefly reveals the label; the label fades when idle and the cursor expires according to existing realtime behavior. No trails, particles, or pulsing.

Cursor packets update a cursor-only subscription store. The cursor layer redraws without rerendering the whole Workspace. Geometry previews still update board geometry and connected lines, so they use the established bounded transport and frame rate.

### Realtime health

- Healthy: no persistent status
- Short interruption: “Reconnecting…”
- Recovered: brief “Back online”
- Longer failure: explain that live collaboration is unavailable and whether saved REST changes still work

Do not expose SignalR terminology or retry counts. Never imply a durable mutation failed only because realtime distribution failed.

### Saving and conflict

Editing, saving, saved, conflict, reconnecting, and realtime-unavailable are distinct states. Avoid persistent “Saved” noise.

A conflict explains what happened, that a remote change exists, whether local work is retained, and the available recovery choices. Never expose “409 Conflict” or silently discard a draft.

Review latest fetches the authoritative note and shows its title/body beside the retained local draft inside the conflict dialog. It does not replace the draft. Keep editing closes the comparison while preserving the browser-session draft; after reviewing latest, a later explicit save uses the current authoritative version.

### Announcements

Meaningful announcements may include editing start/stop and collaboration disconnect/restore. Do not announce cursor coordinates, drag frames, resize frames, lease renewals, or every presence refresh.

Wukna compares user-level editor sets before announcing editing changes, so lease sequence renewals do not create repeated messages. Realtime health uses one polite atomic status; presence and per-note visual indicators do not create competing live regions.

## Member management

Share shows durable members with the same identity color and initials used by presence, cursors, editing indicators, and remote geometry. Each row separates the durable Owner, Editor, or Viewer permission from optional ephemeral “Online now” presence. The owner row has no ordinary mutation actions; ownership transfer is a separate future feature.

Owners open a guest's action menu with right-click, Shift+F10, the platform context-menu key, or the visible Actions button. The same keyboard-navigable menu provides Viewer and Editor choices, marks the current permission, stays within the viewport, closes on outside interaction or Escape, and restores focus to its trigger. On narrow screens it becomes a bottom sheet above the mobile navigation.

Permission changes submit an authenticated REST mutation, then refresh authoritative member data and show a short semantic notice. A downgrade stops active editing and remote geometry previews while the collaborator keeps view access; the next edit is denied by backend authorization. Removing a collaborator requires a separate confirmation explaining immediate access loss. After commit, existing realtime revocation removes their active board connections and board summary. Non-owners see membership but no management actions.

## Responsive behavior

Do not scale desktop down.

- Desktop emphasizes space, relationships, and simultaneous context.
- Tablet balances canvas and progressive disclosure.
- Mobile emphasizes capture, reading, editing, and continuity.

Presence becomes a compact avatar stack and bottom sheet on mobile. Editing remains a small anchored indicator. Desktop-style cursors may be suppressed where they add noise on touch layouts.

Verify representative widths of 320, 375, 390, 430, 768, 1024, 1280, and 1440+ pixels, plus continuous resizing between them. Verify 100%, 125%, 150%, and 200% text/zoom without clipped controls, overlapping labels, inaccessible dialogs, or hidden actions.

## Themes

Light and dark themes are reviewed independently. Do not implement dark mode as an inversion. Text, controls, pigments, collaboration colors, focus, borders, disabled states, connections, remote geometry, and cursors must remain legible in both.

## Motion

Typical timing guidance:

- hover: about 120ms
- panel change: about 180ms
- established connection/state transition: up to about 450ms

`prefers-reduced-motion: reduce` removes nonessential movement. Use opacity, border, color, or instantaneous state changes instead.

## Performance

Keep durable board state, ephemeral geometry, presence, editing, cursors, and realtime health as separate frontend domains. Presentation may combine them without collapsing ownership.

Avoid rerendering the entire board for one presence, editing, geometry, or cursor packet. Avoid layout measurement on high-frequency packets, expensive filters across many nodes, permanent `will-change`, and uncontrolled animation loops.

## Content states

Loading placeholders preserve layout immediately and reveal static content-shaped placeholders after 180ms. Use list, cards, week, calendar, dashboard, chat or form shapes; keep existing content during background refresh. Route features load through native React lazy/Suspense while NotificationRuntime remains mounted. Loading and error states must not claim that a collection is empty.

Loading placeholders approximate final structure. Empty states explain what the area is, what the user can do, and provide one clear action where appropriate. Errors answer what happened, what state the user's work is in, and what they can do next.

An editable empty board offers a direct Add note action in the canvas. Read-only empty boards explain that they are waiting for content. Chat is implemented: preserve composer drafts, history anchors, moderation, attachment scanning and scheduled tasks. Library, Journal and Pictures remain production coming-soon routes with separate sample previews.

## Anti-patterns

Do not introduce:

- legacy dark-cyan dashboard styling
- arbitrary product colors
- color-only status
- undersized icon targets
- drag-only required actions
- invisible focus
- 9–11px product text
- permanent cursor name labels
- connection/tab counts in presence UI
- giant glows or heavy shadows
- ambient canvas animation
- destructive actions beside routine formatting
- desktop collaboration chrome unchanged on phones
- backend or infrastructure terminology in user messages
- broad component rewrites solely for code cleanliness

## Verification baseline

Every material board change should consider:

- keyboard-only operation and focus order
- focus visibility and non-obscuration
- accessible names and status semantics
- light/dark contrast
- 200% scaling
- reduced motion
- touch targets
- non-drag alternatives
- desktop/tablet/mobile behavior
- one and multiple viewers
- editing awareness
- remote drag and resize
- preview-to-commit transition
- unexpected disconnect and reconnect
- same-note version conflict
- access revocation

Automated checks support this review but do not establish WCAG conformance by themselves.

## Primary references

- W3C WCAG 2.2 Understanding documents
- W3C Understanding Dragging Movements
- W3C Understanding Focus Visible
- W3C Understanding Target Size (Minimum)
- Nielsen Norman Group's usability heuristics
- Apple Human Interface Guidelines for layout, typography, accessibility, motion, and controls

These are principles and standards, not visual templates. Wukna remains unmistakably Wukna.

## Future Product Surfaces

The initial personal Tasks page uses the signed-in shell, shared controls, and semantic tokens. Quick add keeps the title visible and reveals date/time controls on demand. Today separates timed tasks from date-only tasks and shows overdue work distinctly. Desktop uses a details dock; smaller screens use a bottom details surface and horizontally scrollable task views. Calendar design and reminder permission flows remain future work.

These patterns define the visual and interaction language for planned features. Product boundaries, status, deferred capabilities, and implementation dependencies live in [FUTURE_FEATURES.md](FUTURE_FEATURES.md). These UI specifications represent planned features and must not be treated as proof of implemented persistence, integrations, upload, or synchronization.

All three surfaces retain Instrument Sans for application controls, Wukna semantic tokens, the 4px spacing scale, existing radii and focus treatment, and the page order of heading → primary tabs → contextual controls → content. The pages have distinct content rhythms while sharing search field spacing, clear behavior, navigation, state language, and responsive behavior. Production uses honest coming-soon pages; `/dev/library`, `/dev/journal`, and `/dev/pictures` use clearly labeled local fixtures for review.

### Library

Library collects material saved from elsewhere. A saved card leads with media, then source and type, title, creator, and saved date. `source` and `type` remain separate concepts: YouTube plus Short, for example. Provider identity stays in small metadata, never a provider-colored page. Standard video uses a wide thumbnail, short video uses a vertical thumbnail, and article or post previews may use flexible image and text proportions. Grid supports visual browsing; list supports title and metadata scanning. Collections are flat, flexible groups with preview mosaics, not a folder hierarchy. The detail surface docks on the right on desktop and becomes a full-width sheet on mobile. Search can eventually span title, caption, creator, source, collection, and tags; source, media type, collection, and date filters should remain restrained.

### Journal

Today is the natural entry point, followed by chronological Entries and a month Calendar. The writing column is centered, spacious, and readable at approximately 17–18px body size with generous line height. Newsreader may emphasize dates, entry headings, and a single reflection prompt; Instrument Sans remains the UI face. Entries show a date, optional title or first line, and short preview with quiet separators. The calendar uses touch-sized days and a non-color dot plus accessible name to indicate sample entries. Prompts are secondary and dismissible. Active writing should have minimal chrome; optional metadata may appear only when explicitly requested. Mobile writing becomes a full-width focused surface. Avoid streaks, scores, and guilt-oriented copy.

### Pictures

Pictures belongs to Library. Photos dominate a responsive grid: roughly four to seven columns on desktop, three to five on tablet, and two to three on phones depending on density. Comfortable and Compact are sufficient density modes. Thumbnail containers may crop for scanning; the full viewer always uses `object-fit: contain` to preserve the source proportion. Albums use quiet preview mosaics. The immersive viewer uses a dark neutral background with Wukna controls and keeps caption, date, album, dimensions, source, and favorite metadata secondary. Selection mode adds a visible check and border plus accessible selection labels and count. Multi-select actions remain disabled in the design preview until storage and mutation behavior exist. No social engagement controls appear.

### Future content states

Skeletons should match the eventual content geometry: thumbnail and metadata rows for Library, text blocks for Journal, and tiles for Pictures. Use a restrained static or reduced-motion-safe treatment. Error states should use the shared Wukna state pattern and a real retry only when a request exists. Empty states explain the purpose without fabricated user content. Preview fixtures must have meaningful image descriptions and always be labeled as samples.
