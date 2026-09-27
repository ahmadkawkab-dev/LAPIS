# Wukna

Wukna is a visual workspace for organizing notes and checklists on shared boards. People can arrange ideas on a canvas, connect related notes, and collaborate with board members in real time.

## What Wukna does

- **Shared boards:** Create, search, rename, and delete boards, and invite members with view or edit access.
- **Visual notes:** Arrange colored notes and task lists, edit checklist items, and draw related or prerequisite connections between notes.
- **Live collaboration:** See who is on a board, follow live cursors and editing indicators, and preview drag or resize changes as they happen. Saved changes persist in PostgreSQL.
- **Accounts and profiles:** Register with email and password or sign in with Google, manage sessions, and update a display name, username, avatar, and interface theme.

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
