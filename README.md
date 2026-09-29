# Wukna

Wukna is a visual workspace for organizing notes and checklists on shared boards. People can arrange ideas on a canvas, connect related notes, and collaborate with board members in real time.

## What Wukna does

- **Shared boards:** Create, search, rename, and delete boards, and invite members with view or edit access.
- **Visual notes:** Arrange colored notes and task lists, edit checklist items, and draw related or prerequisite connections between notes.
- **Live collaboration:** See who is on a board, follow live cursors and editing indicators, and preview drag or resize changes as they happen. Saved changes persist in PostgreSQL.
- **Accounts and profiles:** Register with email and password or sign in with Google, manage sessions, and update a display name, username, avatar, and interface theme.

## Board navigation

Boards use a finite 12,000 × 8,000 workspace centered on the existing origin (X −6,000 to 6,000, Y −4,000 to 4,000). Cards, panning, and edge auto-pan stop at those boundaries. Navigation follows [Figma's canvas gestures](https://help.figma.com/hc/en-us/articles/15297425105303-Explore-design-files), with a fixed page inspired by [Lucidchart's bounded page settings](https://help.lucid.co/hc/en-us/articles/15578781626772-Adjust-document-and-board-settings).

Each user's latest camera position and zoom are saved separately for each board in browser-local preferences, with a 300 ms debounce and a final save when leaving or refreshing. Returning to a board restores that view, clamped to the current board and viewport sizes. A first visit uses fit-to-content. Preferences remain local to that browser; no new server preference schema is needed.

World dimensions are defined in `frontend/src/features/boards/boardBounds.ts` and enforced by `Features/Board/BoardWorkspaceBounds.cs`; keep both definitions aligned when changing the size. Existing out-of-bounds cards are displayed inside the workspace and saved within bounds on their next geometry edit. Connections and SignalR messages retain their existing world-coordinate contracts.

## What's next

Library, Journal, Pictures, and standalone Tasks are planned. Their signed-in routes currently show coming-soon screens; development builds also include sample-content design previews. Board chat is not implemented yet.

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
