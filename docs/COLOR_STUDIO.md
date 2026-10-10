# Color Studio

Color Studio is an official frontend Appearance feature, available in development and production builds. Open **Account → Preferences → Appearance → Color Studio**.

Choose from Warm Minimal, Calm Sage, Soft Lavender, Ocean Slate, Earthy Terracotta, and Modern Neutral. The sample Light/Dark controls and **Compare with original** affect the preview only. **Use this palette across the app** applies the selected palette immediately; choosing another palette while this is enabled updates the app without reloading. Close the panel to explore other pages.

The existing Light/Dark/System preference continues to control the actual app appearance. Each palette has independently coordinated light and dark colors. **Reset to Original Theme** clears the palette selection and restores Wukna's baseline colors for the current appearance.

## Coordinated color families

Each palette uses three distinct families. Dominant color anchors the workspace and canvas, secondary color supports navigation and structural surfaces, and accent color emphasizes actions and focus. The 60/30/10 balance guides visual hierarchy; it is not a pixel-counting rule. The catalog and component samples label these roles, show six board variations, and preview an individual canvas with the original note pigments.

| Palette | Dominant · 60% | Secondary · 30% | Accent · 10% |
| --- | --- | --- | --- |
| Warm Minimal | Warm ivory | Muted sage | Soft terracotta |
| Calm Sage | Cream | Sage green | Golden amber |
| Soft Lavender | Porcelain | Dusty lavender | Muted teal |
| Ocean Slate | Mist gray | Ocean blue | Warm coral |
| Earthy Terracotta | Warm sand | Olive green | Clay orange |
| Modern Neutral | Soft stone | Slate blue | Muted copper |

Light and dark use separately curated family keys. Tonal surfaces and action colors derive from those keys; action ink adjusts against its intended surfaces until readable. Success, warning, information, and destructive colors retain their functional roles. Each appearance checks 156 text, control, focus, and board-variation pairs, including darker card tints and lighter canvas surfaces.

Design references: [60/30/10 as a flexible hierarchy](https://uxbyexample.co.uk/entries/60-30-10-rule/), [balanced color systems](https://ux.tfmstyle.com/guide/use-the-60-30-10-rule-to-build-balanced-color-systems), [Material semantic color roles](https://developer.android.com/codelabs/m3-design-theming), and WCAG guidance for [text contrast](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum) and [control contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast).

## Persistence and scope

Palette choice uses `wukna.palette.v1` in browser local storage, independently of `wukna.theme.v1`. It stores a validated curated palette ID, never arbitrary CSS or account data. Selection survives refreshes and synchronizes between tabs on the same origin. With browser storage blocked, switching and reset still work for the current session.

Selecting a palette changes semantic color and shadow tokens on the document root, including dialog portals. It does not change typography, spacing, canvas geometry, note pigments, collaborator identity colors, or saved board content. The original Wukna palette remains the default until the user explicitly enables a choice.

## Shared board accents

Each board has a shared named color choice. Owners find it under **Manage board** on Boards; editors use **Board appearance**. Viewers see the accent but cannot change it. With the original theme, the six choices are Sage, Blue, Lavender, Clay, Gold, and Rose, and automatic colors derive from the board ID. With Color Studio enabled, the picker names and swatches represent the active palette's six coordinated variations.

The board stores a named accent, not a fixed rendered color or a member's personal theme. The existing six API IDs map to the same variation slots for every member, rendered through each member's selected palette and appearance. Home cards, Boards cards and thumbnails, sidebar indicators, and individual board chrome/canvas share the variation. Card previews carry the strongest tint, chrome is softer, and the canvas has the lightest tint. Thumbnails and canvas notes preserve their original pigments. Original-theme behavior remains unchanged.

## Stable automatic board variations

Automatic boards use six palette-independent slots. IDs are sorted and hashed deterministically; new boards probe unused slots before reusing the least-used slot. Existing relationships and explicit shared choices take priority. Deliberately choosing the same shared color for multiple boards is allowed. Larger collections reuse the six slots rather than generating unbounded arbitrary colors.

Assignments persist as validated UUID → slot pairs in `wukna.board-variations.v1:<account-id>`. Navigation, reload, board reorder, palette changes, and Light/Dark changes preserve those relationships. History retains up to 2,048 entries, including recently absent boards. Account changes isolate the history, same-origin storage events synchronize tabs, and blocked storage uses a session fallback. Reset removes palette-specific rules while retaining assignments for a later palette switch.

Automatic assignment history is personal to an account on this browser. Different devices or members with different histories can assign automatic slots differently; shared manual choices use the server's common slot. This update adds no backend fields or migration. It continues using the existing authorized, versioned shared appearance contract described below.

Color updates replace one scoped stylesheet only when rules change. They do not remount the canvas, alter positions, zoom, selection, drag state, note content, or link geometry. Only validated UUIDs become selectors; stored values cannot provide arbitrary CSS.

`PUT /api/boards/{boardId}/appearance` accepts `{ "cardColor": "sage", "expectedVersion": 0 }`. `cardColor` may be one of the six lowercase accent IDs or `null` for automatic. Both properties are required. Successful writes return the updated board detail with `cardColor` and `cardColorVersion`; list/detail DTOs and the existing `BoardUpdated`/`BoardSummaryChanged` events carry the same values.

The backend checks persisted membership and edit permission while holding the existing board-before-membership locks. It commits before realtime publication. A stale version receives `409 board_appearance_conflict`; the frontend reloads the latest board and prompts the user to choose again. Failed saves retain the previous displayed color. No-op writes do not advance the version or activity time. Existing membership revocation and summary recipient protections remain in use.

Migration `20261009181230_AddBoardCardAppearance` adds nullable `boards.card_color`, an integer `card_color_version` with default zero, and constraints for supported accent IDs and nonnegative versions. Existing boards keep their content and start in automatic mode. The API deployment requires this migration before the new model serves requests.

## Implementation

- `frontend/src/theme/coordinatedPalettes.ts`: three-family light/dark definitions and six tonal board variations per palette.
- `frontend/src/theme/curatedPalettes.ts`: palette catalog and semantic/board contrast checks.
- `frontend/src/theme/paletteState.ts`: root token application, local persistence, theme synchronization, exact reset, and original-theme sample values.
- `frontend/src/theme/boardVariations.ts` and `boardVariationState.ts`: deterministic assignments, per-account history, and scoped board CSS updates.
- `frontend/src/styles/palettes.css`: supporting surfaces, board gradients, and sidebar indicators, active only with a selected palette.
- `frontend/src/components/appearance/ColorStudio.tsx`: lazy-loaded Appearance UI and scoped component samples.
- `frontend/src/features/boards/boardCardTone.ts`: stable card accent selection.
- `frontend/src/features/boards/BoardCardColorControl.tsx`: shared color picker and save/conflict feedback.
- `Features/Board/BoardAppearanceEndpoints.cs`: authorized, versioned shared accent writes.

There is no additional theme provider or dependency. The root tokens remain shared across the application; candidates override only an explicit set of color tokens. Samples inherit the same component styles with their own scoped token values. Contrast feedback checks text pairs at 4.5:1 and focus/control boundaries at 3:1, and visibly warns about unsafe candidates. These checks are not a complete accessibility certification.

Custom user palettes are a future extension. This release provides curated theme and accent choices only; it does not include a custom theme editor, custom-palette persistence, account theme synchronization, or a marketplace. Shared board accents already persist in the backend. Any future custom palette must validate the allowed token set and both appearances before being treated as ready for everyday use.

## Verification

Run `npm test` and `npm run build` from `frontend`. The tests check all curated light/dark pairs, six distinct variations, collision handling, larger collections, shared-choice priority, contrast warnings, production initialization, persistence isolation, invalid storage, blocked storage, cross-tab changes, theme synchronization, unchanged canvas DOM/content, and exact restoration.

The explicit Opera checks use a disposable browser profile and synthetic API fixtures: `frontend/tests/browser-color-studio.mjs` checks scoped comparison, hover/focus, major routes, note colors and geometry, reset, contrast warnings, and the production preview. `browser-color-studio-responsive.mjs` adds narrow/tablet/wide layouts, filled/empty thumbnails, and enlarged text. `browser-board-colors.mjs` checks six shared accent choices across all themes, navigation/reload, permission-specific controls, failed saves, conflict recovery, and retry.

`browser-board-variations.mjs` verifies consistent automatic colors between Home, Boards, and individual canvases, persistence across reload, palette updates across tabs without remounting the board world, the spaced collapsed sidebar, and the functioning search shortcut without its visual hint. Calendar checks cover a 24px current-date marker inside a target at least 44px tall, centered at narrow and landscape mobile widths. At enlarged text sizes the marker grows with the text while fitting its column; paging controls, compact weekday labels, long agenda titles, and navigation labels reflow instead of overlapping.

Set `WUKNA_OPERA_CDP`, `WUKNA_PLAYWRIGHT_MODULE`, and `WUKNA_AUDIT_OUTPUT` for the local browser/runtime; serve development on port 5173 and a fresh production preview on port 4174 (or set `WUKNA_PRODUCTION_PREVIEW`). Browser fixtures do not establish backend persistence. `BoardAppearanceEndpointTests` separately use real PostgreSQL migrations, authenticated HTTP requests, and SignalR clients to verify persistence, access control, concurrent writes, reset, migration compatibility, and member-only realtime delivery.

Production inclusion is implemented in source. This change does not deploy Wukna.
