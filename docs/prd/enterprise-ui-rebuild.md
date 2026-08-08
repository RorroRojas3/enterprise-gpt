# PRD: Enterprise GPT Frontend Rebuild

## 1. Overview

**Problem.** The `enterprise-ui` client has drifted past the point where migration pays. The API has grown projects, conversation favorites, a document ingest and download pipeline, model and MCP catalogs, admin user management, and — as of the current working tree — a fully framed assistant-status stream. The client consumes almost none of it, holds 17 writable signals in a single root service across four unrelated domains, has zero tests, and renders model output through `bypassSecurityTrustHtml` with `html: true`. It is also **already broken**: `docs/conversations/streaming-contract.md` §7 records that the framed SSE release breaks `ConversationService.streamChat` knowingly and with the user's agreement, so the current client appends literal `data: {"kind":"TextDelta",…}` frames into chat bubbles.

**Solution.** Delete `enterprise-ui/` and regenerate it at the same path from `ng new` on Angular 21.2.19 zoneless, with `@ngrx/signals` 21.1.1, Bootstrap 5.3, MSAL v6, and Vitest. Nothing is migrated. The rebuilt client covers chat with a live chronological activity timeline, projects, documents, and an admin area — and every backend capability that is missing today is either delivered as an `[enabler]` story in this document or shipped as a visible unavailable state, never as mock content.

**Success criteria.**

- **Streaming fidelity**: all eight `AssistantUiEventKind` values fold correctly, measured by unit tests over recorded event fixtures in `domain/stream/`, including a fixture where one `data:` frame is split across two `read()` chunk boundaries. Target: 100% of the eight kinds covered, 0 failures.
- **Content safety**: an assistant response containing `<img src=x onerror=alert(1)>`, `<script>`, and `javascript:` URLs renders as inert text with no script execution, measured by unit tests against `MarkdownService` plus a lint rule that fails the build if `bypassSecurityTrustHtml` appears anywhere outside that one file. Target: 1 permitted call site, 0 executions.
- **No inherited defects**: the four known defects of the old client do not reappear, measured by four named regression tests — conversation search sends `name=` and filters; `mcpServerIds` restores the MCP selection on reopen; a rename echoes `projectId` and does not unlink the conversation; a production build resolves the API base URL from `config.json` and never from a compiled-in `localhost:7045`. Target: 4 of 4 passing.
- **Accessibility**: 0 serious or critical axe-core violations on the chat, projects, documents, and admin routes in both themes, measured by the automated axe run added in US-1405.
- **Capability honesty**: every feature with no backing API renders `UnavailablePanelComponent`, measured by a test asserting that `DocumentLibraryStore` and `ReportsStore` expose no seeded or placeholder records. Target: 0 mock records rendered anywhere in the app.

A signed-in user lands on an empty prompt box, types, and watches the answer arrive alongside a chronological list of what the assistant is doing — reasoning, a function call, an MCP tool, an agent and its children — each card labelled with its display name and a separate kind badge. The conversation is created on that first prompt without remounting the page, the server names it out of band, and the name appears in the sidebar a moment later. The user can stop mid-answer and keep the partial text as a detached card that says plainly it was not saved. Their conversations group into projects; their uploads carry staged progress; administrators manage users, models, and MCP servers from deep-linkable tabs.

## 2. Goals & non-goals

**Goals.**

- Replace `enterprise-ui/` with a client that consumes the current API surface in full, including the framed SSE stream that the existing client cannot read.
- Render the assistant's work — reasoning, function calls, MCP tool calls, sub-agent calls — as a chronological timeline built on the contract the server already emits, not on a protocol invented for this project.
- Close the ten backend capability gaps that block requested features, each as an `[enabler]` story inside the feature epic it unblocks, so the Documents screen, response feedback, and usage reports have a path to working rather than a permanent placeholder.
- Make every list screen honest about what the API can and cannot do, rather than offering sort and filter controls the server does not support.
- Establish a state layer on `@ngrx/signals` that new features extend by composition, replacing the single mutable root service the old client grew.
- Ship dark and light theming, three responsive breakpoints, and keyboard and screen-reader support as acceptance criteria on the stories that introduce each surface, not as a cleanup pass.

**Non-goals.**

- Migrating any code, component, style, or test from the current `enterprise-ui/`. The directory is deleted; `docs/ui/mockups/` is the only surviving UI reference.
- Server-side rendering, hydration, or a PWA/offline mode.
- Changing the SSE wire format, the `Andes.Extensions.AI.UI` event contract, or the RFC 9457 problem type URIs — those are opaque identifiers clients match verbatim, and changing one is a breaking API change.
- Resumable streams, `Last-Event-ID`, or reconnect-and-continue. The stream is explicitly not resumable (streaming contract §6.3); a dropped connection cancels the turn.
- Persisting or replaying the activity tree for past turns. Only the answer text is transcribed (streaming contract §6.2); reopening a conversation replays the answer, never the work that produced it.
- Virtualizing the transcript. Transcripts are bounded by the Cosmos 2 MB item cap, and a virtual scroller fights the streaming tail and breaks in-page find.
- Real-time collaboration, multi-user conversations, or conversation sharing between users.
- Adding EF Core migrations. `Repository/Migrations/` is empty and `Database.Migrate()` runs at startup; the enabler stories here add only columns and endpoints that the existing `EnsureCreated`/`Migrate` path already covers, and the first real migration is flagged separately.

## 3. Users & access

**Personas.**

- **Chat user**: a signed-in employee who holds conversations with the model, uploads documents into them, groups them into projects, and selects models and MCP tool servers per turn. The default persona; every user is one.
- **Administrator**: a chat user who additionally holds the `Administrator` permission (`a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d`) and manages the user directory, the model catalog, the MCP server catalog, and usage reports.
- **Backend engineer**: owns the `[enabler]` stories in this document — the ten API capabilities the frontend features depend on. Works in `enterprise-gpt-api/` under `.claude/rules/csharp.md` and `aspnet-rest-apis.md`.
- **Frontend engineer**: owns every non-enabler story, working in the regenerated `enterprise-ui/` under the `ngrx-signal-store` and `angular-developer` skills.

**Role-based access.**

- **Anonymous**: may reach `/auth` and `/login-failed` only. Every other route is behind `MsalGuard` plus `sessionGuard`.
- **Chat user**: full access to their own conversations, projects, and documents. Ownership is enforced server-side and a resource belonging to another user returns 404, not 403, so the client must never treat 404 as "deleted" in a way that leaks existence.
- **Chat user with `Upload File`** (`b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e`): may upload into conversations and projects. Seeded as a default grant, but it is revocable and **administrators are not implicitly granted it** — the client checks it explicitly before rendering any upload affordance.
- **Administrator**: additionally reaches `/admin` and its four tabs. The admin route chunk is guarded with `canMatch` so a non-admin never downloads it. Two server guardrails return 400 and need friendly copy: an administrator cannot revoke their own `Administrator` permission, and cannot deactivate themselves.
- **MCP-derived permissions**: each MCP server may carry a `PermissionId`; `GET /api/mcps` returns only the servers the caller is permitted, so the MCP picker needs no client-side filtering.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | Users authenticate with Entra ID through MSAL redirect; no application route renders before an active account exists | P0 | EP-2 |
| FR-2 | Sign-in bootstraps through `POST /api/users/me`, then loads permissions, models, permitted MCP servers, conversations, and projects | P0 | EP-2 |
| FR-3 | The admin route chunk is never downloaded by a user without the `Administrator` permission | P0 | EP-2 |
| FR-4 | Upload affordances render only when the caller holds `Upload File`, checked explicitly and never inferred from `Administrator` | P0 | EP-2, EP-8 |
| FR-5 | A collapsible sidebar lists conversations and persists its collapsed state across reloads | P0 | EP-3 |
| FR-6 | Sidebar conversation rows support rename, favorite/unfavorite, soft-delete, and project assignment/unassignment | P0 | EP-3 |
| FR-7 | "New conversation" opens an empty composer; the conversation is created only on the first prompt, without remounting the page | P0 | EP-3, EP-4 |
| FR-8 | The composer offers file and image attachment, a model picker, an MCP picker, voice input, and a send/stop control | P0 | EP-4, EP-8 |
| FR-9 | MCP selection is disabled when the selected model reports `isToolEnabled === false` | P0 | EP-4 |
| FR-10 | The client parses the framed SSE stream from `POST /api/conversations/{id}/stream` and renders answer text incrementally | P0 | EP-4 |
| FR-11 | Stop aborts the request; the partial answer is retained outside the transcript, labelled as not saved, with Copy and Retry | P0 | EP-4 |
| FR-12 | A stream that ends without a `Finished` event is reported as an incomplete turn, since that is the only truncation signal available | P0 | EP-4 |
| FR-13 | A 409 `conversation-busy` is surfaced as a retryable warning and never auto-retried | P1 | EP-4 |
| FR-14 | The server-generated conversation title appears after the first turn without a manual refresh | P1 | EP-4 |
| FR-15 | Reopening a conversation restores its last model and MCP server selection from `ConversationDetailDto` | P1 | EP-4 |
| FR-16 | An MCP consent requirement is explained to the user and never triggers a token refresh | P1 | EP-4 |
| FR-17 | Reasoning, function calls, MCP tool calls, and sub-agent calls render as separate cards in the order they happened, interleaved with answer text | P0 | EP-5 |
| FR-18 | Child activities render nested inside their parent card; sub-status lines, duration, and failure state are shown | P1 | EP-5 |
| FR-19 | Per-turn token usage from the `Finished` event is displayed | P1 | EP-5 |
| FR-20 | Model output is rendered as markdown with raw HTML disabled and the result sanitized before insertion | P0 | EP-6 |
| FR-21 | Code blocks render with theme-matched syntax highlighting and a copy control | P1 | EP-6 |
| FR-22 | Diagrams and math render on demand and are absent from the initial bundle | P2 | EP-6 |
| FR-23 | The transcript stays pinned to the newest content unless the user scrolls up, in which case a jump-to-latest control appears | P0 | EP-6 |
| FR-24 | A user can copy their own prompt and the assistant's response | P1 | EP-6 |
| FR-25 | The conversations screen supports search by name, offset pagination, favorites filtering, and multi-select bulk delete | P1 | EP-7 |
| FR-26 | No list screen offers a sort control the API cannot honor; where sorting is offered over a truncated set, the truncation is stated | P1 | EP-7, EP-9 |
| FR-27 | Files attach to a conversation or project and their ingest progress is polled on a staged schedule | P1 | EP-8 |
| FR-28 | An expired or unknown upload job renders differently from a failed one | P1 | EP-8 |
| FR-29 | Document download fetches the signed URL on click only; the URL is never prefetched, persisted, logged, or placed in a router URL | P1 | EP-8 |
| FR-30 | The projects screen supports search by name, rename, description editing, and soft-delete | P1 | EP-9 |
| FR-31 | Project detail edits instructions, adds and removes project files, starts a conversation in the project, and lists that project's conversations with per-row actions | P1 | EP-9 |
| FR-32 | Projects can be favorited and favorited projects appear in the sidebar with their linked conversations | P2 | EP-9 |
| FR-33 | A documents screen lists documents uploaded to any conversation, filterable by name and groupable by conversation | P1 | EP-10 |
| FR-34 | Documents created by the model are distinguishable and filterable | P2 | EP-10 |
| FR-35 | An assistant response can be rated thumbs up or thumbs down, anchored to a stable message identity | P2 | EP-11 |
| FR-36 | Admin users tab supports paginated search over name and email, create, update, deactivate, and permission-set editing | P1 | EP-12 |
| FR-37 | Admin users can be filtered by permission | P2 | EP-12 |
| FR-38 | Admin models tab supports add, update, soft-delete, and client-side search by provider and model name | P1 | EP-12 |
| FR-39 | Admin MCP tab supports full CRUD over the administrative MCP server representation | P1 | EP-12 |
| FR-40 | Admin tabs are deep-linkable routes, each lazy-loading its own store | P1 | EP-12 |
| FR-41 | Reports are fetched on every entry to the tab and never served from cache | P2 | EP-13 |
| FR-42 | The app supports dark and light themes with no light-mode frame painted before a dark-mode load | P0 | EP-1 |
| FR-43 | Configuration is fetched at runtime before bootstrap; no environment value is compiled into the bundle | P0 | EP-1 |
| FR-44 | All ten RFC 9457 problem types the API emits are typed and handled, including `provider-not-configured` and `storage-not-configured` | P0 | EP-1 |
| FR-45 | The app is zoneless and every component uses `OnPush` | P0 | EP-1 |
| FR-46 | The initial bundle stays within budget and the build fails if the diagram or math chunk enters it | P0 | EP-1 |
| FR-47 | Every interactive surface is operable by keyboard, streaming output is announced to assistive technology, and motion honors `prefers-reduced-motion` | P0 | EP-14 |
| FR-48 | The app is usable at ≥1024px, 768–1023px, and <768px with the sidebar behavior defined per breakpoint | P0 | EP-14 |
| FR-49 | Every capability with no backing API renders a visible unavailable state and no placeholder data | P0 | EP-1, EP-10, EP-13 |

## 5. User experience

**Entry points & first-time flow.** The user opens the app URL and is redirected to Entra ID. On return, `/auth` completes the MSAL redirect, `sessionGuard` resolves the session by calling `POST /api/users/me`, and the shell renders with the sidebar populated. A first-time user has no conversations, so the sidebar shows an empty state and the chat route shows the centered wordmark, the "How can I help you today?" subtitle, and suggested prompt chips per `docs/ui/mockups/index.html`.

**Core experience.**

1. The user types in the composer, optionally attaches files, picks a model, and toggles MCP servers.
2. Send posts to the stream endpoint. On a brand-new conversation the client first creates it, then updates the URL in place via `Location.replaceState` — a single `UrlMatcher` serves both `/chat` and `/chat/:conversationId` so the page does not remount and composer state survives.
3. The answer streams in. Between request-accepted and the first activity or text delta the timeline is empty, because this application emits no request-level `Status` events of its own (streaming contract §9) — that gap is filled by a thinking indicator, not by a fabricated status line.
4. Activity cards appear in arrival order interleaved with text. Each card shows `displayName` as its label with `kind` as a separate badge, `source` as a subtitle, `subStatuses[]` beneath, children nested, and `durationSeconds` on completion.
5. `Finished` ends the turn, carrying `usage`. The answer is re-rendered once, canonically, with full syntax highlighting.
6. On a first turn, the server names the conversation out of band and does not send the name down the stream, so the client refetches `GET /api/conversations/{id}` and the sidebar row updates.

**Edge cases & UI states.**

- **Empty**: no conversations, no projects, no documents, no search results — each surface has a designed empty state, distinct from a loading skeleton.
- **Loading**: skeleton rows for lists; a thinking indicator for the pre-first-delta window; per-row spinners that do not disable the rest of the list.
- **Stopped**: partial answer as a visually detached card reading "Stopped — not saved to this conversation", with Copy and Retry. Dropped on reopen, route change, and refresh. The optimistic user bubble rolls out too, because a cancelled turn transcribes neither the answer nor the prompt.
- **Truncated**: a body that ends with no `Finished` event. Indistinguishable from a network drop after the first frame, so it renders as "This response was cut off" with Retry — never as a completed answer.
- **Busy**: 409 `conversation-busy` renders a transient warning with a retry affordance and no automatic retry.
- **Consent required**: 403 `mcp-authorization-required` renders an explanation naming the `serverName` extension. It must never trigger a token refresh — a refresh loop cannot satisfy an interactive consent requirement and will spin.
- **Unavailable**: `UnavailablePanelComponent` for every capability-gated feature, naming what is missing and showing no data.
- **Upload expired**: a 404 on `GET /api/documents/upload-status/{jobId}` after the retention window means expired or unknown, and reads differently from a `Failed` state.
- **Fatal**: a failed `config.json` fetch renders a static fatal shell in `index.html` rather than a blank page.

**UI/UX highlights.** Bootstrap 5.3 utilities carry the layout, with custom CSS confined to overriding Bootstrap's defaults toward the neutral palette in `docs/ui/mockups/enterprise-gpt-requirements-v2.md`. Theme is applied by an inline script before first paint. Bootstrap's ESM components (`Dropdown`, `Modal`, `Offcanvas`) are imported per component rather than loaded as global scripts. Toasts are top-right at `top: 76px; right: 20px`, four types, 5-second auto-dismiss, 8px stacking gap. Transitions are 150ms; sidebar 200ms; message appear 200ms; all suppressed under `prefers-reduced-motion`.

## 6. Technical considerations

**Integration points.** All real, verified against the working tree.

| Concern | Where |
| --- | --- |
| Chat turns, transcript, conversation list, favorite, bulk delete | `Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs` — a minimal-API module. There is no `ConversationsController`; the last MVC controller was removed |
| SSE framing and streaming headers | `ConversationEndpoints.StreamConversationAsync`, documented end to end in `docs/conversations/streaming-contract.md` |
| Event type and TypeScript mirror | `Andes.Extensions.AI.UI` 0.5.0; contract at `~/.nuget/packages/andes.extensions.ai.ui/0.5.0/typescript/andes-assistant-ui.ts` |
| Projects, project documents | `Enterprise.Gpt.Api/Endpoints/ProjectEndpoints.cs` |
| Upload, upload status, download, supported extensions | `Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs` |
| Model catalog | `Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs` — `GET ""` for permitted, `GET "all"` for admin |
| MCP catalog | `Enterprise.Gpt.Api/Endpoints/McpEndpoints.cs` — `GET ""` returns `McpDto`, `GET "all"` returns `McpServerDto` |
| Users, self-provisioning | `Enterprise.Gpt.Api/Endpoints/UserEndpoints.cs` — `POST me` is the bootstrap call |
| Permissions and grants | `Enterprise.Gpt.Api/Endpoints/PermissionEndpoints.cs` |
| Problem type URIs | `Enterprise.Gpt.Api/Problems/ProblemTypes.cs` |
| Usage audit data | `ConversationUsage`, `ConversationUsageToolCall`; documented in `docs/conversations/usage-and-favorites.md` |
| Design reference | `docs/ui/mockups/` — `enterprise-gpt-requirements-v2.md` plus `index.html`, `conversations.html`, `administrator.html` |

The response envelope for every paginated list is `PaginatedResponseDto<T>`: `{ items, totalCount, pageSize, currentPage, totalPages }`, with `take` clamped server-side to 1–100.

**Data storage & privacy.** Conversation message history lives in Cosmos DB partitioned by `/userId` via the raw `CosmosClient`; everything else is SQL Server 2025 with soft delete on nullable `DateDeactivated` and no query filters. Client-side, `localStorage` holds only the theme, the sidebar collapsed state, and — until B3 lands — device-local project pins; no token, conversation content, or document metadata is written to it. The stream carries no prompt content, tool arguments, or tool results by design (streaming contract §6.1), so nothing the timeline renders is user input echoed back. `DocumentDownloadDto.downloadUrl` is a bearer credential with roughly a five-minute TTL: fetched on click only, never prefetched for a list, never persisted, never logged, never placed in a router URL. The download route already sends `Cache-Control: no-store` and signs `Content-Disposition`, so an `<a download>` is sufficient.

**Security.**

- One functional `authInterceptor` over one `TokenService`. `MsalInterceptor` and `withInterceptorsFromDi()` are not used, because the raw `fetch` that reads the stream bypasses `HttpClient` interceptors entirely and both paths must share one token source.
- `html: false` on `markdown-it` plus DOMPurify with an explicit profile, and exactly one `bypassSecurityTrustHtml` call site inside `MarkdownService`, pinned by a `no-restricted-syntax` lint rule. Model output is attacker-influenceable through uploaded documents and MCP tool results, so this is the primary trust boundary in the client.
- Stores hold text, never `SafeHtml`. Sanitized HTML is a view concern; keeping it in state is unserializable and doubles transcript memory.
- `/problems/mcp-authorization-required` arrives as 403 and must never enter the token-refresh path.
- Retry only idempotent GETs, only on 502/503/504, with jittered backoff. Never auto-retry 409.
- MSAL is a two-major jump from the old client (`msal-angular` 4 → 6, `msal-browser` 4 → 5); the configuration is written against the v6 API rather than ported.

**Scalability & performance.**

- Decoded stream events are buffered on a ~16ms budget and folded as one batch, so a turn produces at most one `patchState` per frame regardless of token rate. `bufferTime` rather than `requestAnimationFrame`, because the former is deterministic under test without a browser scheduler.
- Streaming text lives in one string on `TurnStore` and is appended to the transcript exactly once, on completion — a chunk must not cost O(transcript).
- Markdown rendering splits head/tail at the last block boundary (`lastIndexOf('\n\n')`, or the fence open inside an unclosed fence), memoizes the head by length, re-renders only the tail per flush, and skips highlighting on the tail. The single canonical full render happens on `Finished`.
- Upload polling escalates 500ms → 1s → 2s → 4s, capped at 5s. Server progress is monotonic; a local value never moves backwards.
- Auto-scroll uses an `IntersectionObserver` on a zero-height bottom sentinel with `rootMargin: '0px 0px 80px 0px'`, matching the mockup's 80px threshold — not a `(scroll)` binding, which would dirty the component on every scroll event. The transcript sets `overflow-anchor: none` and `overscroll-behavior: contain`.
- No endpoint accepts a sort parameter today, so client-side sort is offered only over a fully materialised set. Four regimes apply: **A** full-set client sort after draining at `take=100` to a 500-item ceiling (projects, project conversations); **B** server order with no sort control (conversations list); **C** unpaginated, fully correct client sort and search (admin models, admin MCPs, via the `/all` routes); **D** server-paged with search and pagination only (admin users). Sorting only the loaded page is not an option — "Load more" would append a second sorted run underneath it.

**AI system requirements.** The client consumes the stream; it does not call a model directly. The evaluation surface is therefore the fold and the codec rather than model quality:

- `foldAssistantEvents` is vendored verbatim from the package and must not be reimplemented. A CI step diffs the vendored copy against `~/.nuget/packages/andes.extensions.ai.ui/0.5.0/typescript/andes-assistant-ui.ts` and fails on drift.
- Codec and timeline tests run against recorded frame fixtures with no backend: Node 20+ supplies `ReadableStream`, `TextDecoder`, `AbortController`, and `fetch` natively.
- Pass threshold: all eight event kinds folded correctly, an unknown `kind` ignored rather than thrown on, and a frame split across a chunk boundary reassembled — 100% of these cases green before the transport story is accepted.
- Activity labels are never pre-composed. `displayName` and `kind` are rendered separately; a string like "Calling Weather MCP" is a defect, because the contract keeps them apart precisely so the kind word is not repeated.

## 7. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | Foundation and application shell | The app boots from runtime config, renders a themed shell, and fails safely | P0 | L | — |
| EP-2 | Sign-in and session | A user signs in with Entra ID and the client knows who they are and what they may do | P0 | M | EP-1 |
| EP-3 | Navigation and conversation sidebar | A user reaches and manages their conversations from a persistent sidebar | P0 | M | EP-2 |
| EP-4 | Chat composition and turn execution | A user sends a prompt and receives a streamed answer they can stop | P0 | L | EP-1, EP-2 |
| EP-5 | Assistant activity timeline | A user sees what the assistant is doing, chronologically, as it happens | P0 | M | EP-4 |
| EP-6 | Answer rendering and reading | A user reads a safe, formatted answer and stays oriented while it streams | P0 | M | EP-4 |
| EP-7 | Conversation library | A user finds, filters, and bulk-manages conversations on a dedicated screen | P1 | M | EP-3 |
| EP-8 | Attachments, upload, and download | A user attaches files to a turn or project and retrieves them later | P1 | M | EP-2, EP-4 |
| EP-9 | Projects | A user groups conversations, files, and standing instructions into projects | P1 | L | EP-3 |
| EP-10 | Documents library | A user browses every document they have uploaded, across conversations | P1 | M | EP-8 |
| EP-11 | Response feedback | A user rates an assistant response and the rating is recorded | P2 | M | EP-4 |
| EP-12 | Administration | An administrator manages users, models, and MCP servers | P1 | L | EP-2 |
| EP-13 | Usage reports | An administrator sees what the platform is spending and on what | P2 | L | EP-12 |
| EP-14 | Access and conformance gates | The app is operable by keyboard and screen reader, at every breakpoint, within budget | P0 | M | EP-6, EP-9, EP-12 |

EP-1 carries an unusually high proportion of `[enabler]` stories. That is a property of a greenfield rebuild from an empty directory rather than a decomposition failure: each enabler names the stories it unblocks, and none of them exceeds M.

### EP-1: Foundation and application shell

#### US-101: `[enabler]` Scaffold the zoneless Angular 21 workspace

- **Story**: Regenerate `enterprise-ui/` from `ng new` on Angular 21.2.19 with zoneless change detection and Vitest, so every later story is written against the target runtime rather than migrated onto it. Unblocks every story in this document.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given the regenerated workspace, when `package.json` and the build and test `polyfills` are inspected, then `zone.js` appears in neither, and `provideZonelessChangeDetection()` is present in `app.config.ts`.
  - Given the workspace, when the `test` target is inspected, then it uses `@angular/build:unit-test` with the Vitest runner and `npm test` passes with zero specs.
  - Given the workspace, when dependencies are resolved, then `@ngrx/signals` 21.1.1, `@ngrx/operators`, `@azure/msal-angular` 6, `@azure/msal-browser` 5, `bootstrap` 5.3, `markdown-it` 14, and `dompurify` 3.4+ install with no peer-dependency warnings, and neither `@angular/animations` nor `@angular/platform-browser-dynamic` is present.
  - Given `tsconfig.json`, when compiled, then `strict`, `strictTemplates`, `noPropertyAccessFromIndexSignature`, and `noUncheckedIndexedAccess` are all enabled and the build produces zero errors.
  - Given `angular.json`, when inspected, then there is no global `scripts: [popper, bootstrap]` entry.

#### US-102: `[enabler]` Load configuration at runtime before bootstrap

- **Story**: Fetch `config.json` before `bootstrapApplication` so one build artifact is promoted across environments. Unblocks US-201 and US-405, and is the fix for the old client shipping a production build pointed at `localhost:7045`.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given a served app, when `main.ts` runs, then `config.json` is fetched with `cache: 'no-store'`, validated by a narrow assertion, and provided through an `APP_CONFIG` injection token before `bootstrapApplication` is called.
  - Given a `config.json` that is missing, unreachable, or fails validation, when the app loads, then a static fatal shell renders with an explanatory message and the app does not bootstrap — a blank page is a failure of this criterion.
  - Given a production build, when the output is searched, then no environment-specific host appears in any compiled chunk and no `environment*.ts` or `fileReplacements` entry exists.
  - Given `config.json`, when read, then it carries the API base URL, the MSAL client and authority values, and the feature flags for the diagram and math chunks.

#### US-103: `[enabler]` Type and normalize every API error

- **Story**: Model the API's RFC 9457 responses as typed values so stores and forms consume one `AppError` shape. Unblocks every store that calls the API.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101
- **Acceptance criteria**:
  - Given the ten problem types in `ProblemTypes.cs`, when the client constants are compared, then all ten are present verbatim — `validation-error`, `upload-too-large`, `resource-not-found`, `forbidden`, `permission-required`, `conversation-busy`, `mcp-authorization-required`, `mcp-server-unavailable`, `provider-not-configured`, `storage-not-configured` — each under the relative base `/problems/`.
  - Given a problem body, when it is typed, then the extensions `maxBytes`, `permissions[]`, `serverName`, `providerId`, `traceId`, and `instance` are all reachable without a cast.
  - Given an `HttpErrorResponse`, a `fetch` `Response`, or an unknown thrown value, when `toAppError()` is applied, then the result is one discriminated `AppError` and the error toast's detail line shows the `traceId`.
  - Given a 400 carrying an `errors` dictionary keyed by property name, when `applyServerErrors(form, problem)` runs, then each key maps onto the matching reactive-form control's error state.
  - Given a 403 whose `type` is `/problems/mcp-authorization-required`, when the interceptor handles it, then no token refresh is attempted and the error is passed through unchanged.
  - Given a 502 on a GET, when it is retried, then backoff is jittered and capped; given a 409, when it is handled, then no automatic retry occurs.

#### US-104: `[enabler]` Build the reusable signal-store features

- **Story**: Write the five composable `@ngrx/signals` features every store depends on, with specs, before any store exists. Unblocks all 16 stores.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101
- **Acceptance criteria**:
  - Given `withRequestStatus`, when state is inspected, then status is one value across `idle | pending | fulfilled | { error }` so "loading and errored" is unrepresentable, and standalone `setPending()`, `setFulfilled()`, and `setError()` updaters exist.
  - Given `withOffsetPagination`, when applied against a `PaginatedResponseDto` envelope, then `skip`, `take`, `totalCount`, `hasMore`, and `isFullyLoaded` are derived correctly, including the boundary where `totalCount` is an exact multiple of `pageSize`.
  - Given `withPendingIds`, when one row's action is in flight, then only that row reports pending and the rest of the list stays interactive.
  - Given `withClientQuery`, when `isAuthoritative` is false, then sort is unavailable and the reason is exposed for the UI to render.
  - Given `withResetOnSignOut`, when `sessionEvents.signedOut` fires, then the composing store returns to its initial state.
  - Given all five, when reviewed, then `protectedState` is on, updaters are standalone `PartialStateUpdater` functions, feature order is `withState → withProps → withComputed → withMethods → withHooks`, and each has passing Vitest specs.

#### US-105: Switch between light and dark without a flash

- **Story**: As a user, I want the app to open in my chosen theme immediately so that I never see a light frame before a dark page.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given a stored dark-theme preference, when the app loads, then `data-theme="dark"` is set by an inline script in `index.html` before first paint and no light-mode frame is painted at any point during load.
  - Given no stored preference, when the app loads, then `prefers-color-scheme` decides the initial theme.
  - Given the navbar toggle, when it is used, then the theme changes, the choice is written to `localStorage`, and it survives a reload.
  - Given a theme change, when a code block is visible, then the highlight.js stylesheet swaps to the matching variant in the same frame.

#### US-106: `[enabler]` Build the shared UI kit

- **Story**: Provide the wrapped Bootstrap primitives and state components every feature screen composes. Unblocks EP-3 and every screen thereafter.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101, US-105
- **Acceptance criteria**:
  - Given the kit, when inventoried, then modal, toast, dropdown, offcanvas, paginator, debounced search input, file chip, empty state, skeleton, and `UnavailablePanelComponent` all exist as standalone `OnPush` components.
  - Given a component using a Bootstrap behavior, when the source is inspected, then `Dropdown`, `Modal`, or `Offcanvas` is imported as an ESM symbol in that component rather than loaded from a global script.
  - Given a toast, when raised, then it appears top-right below the navbar, carries a 4px type accent border and an icon for one of success, error, warning, or info, auto-dismisses after 5 seconds, and stacks with an 8px gap.
  - Given `UnavailablePanelComponent`, when rendered, then it names the missing capability and displays no rows, records, or placeholder values.
  - Given a modal, when it opens, then focus moves into it and returns to the invoking control on close.

#### US-107: `[enabler]` Vendor the Andes streaming contract with a drift check

- **Story**: Copy the package's TypeScript contract and reducer into the app verbatim, and fail CI if it diverges from the installed package. Unblocks US-404 and all of EP-5.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given the vendored file at `src/app/domain/stream/andes/assistant-ui.contract.ts`, when compared byte-for-byte against `~/.nuget/packages/andes.extensions.ai.ui/0.5.0/typescript/andes-assistant-ui.ts`, then only an added header comment pinning version `0.5.0` differs.
  - Given the vendored file, when it drifts from the package copy, then the CI diff step fails the build with a message naming both paths.
  - Given the codebase, when searched, then no hand-written declaration of `AssistantUiEvent`, `AssistantActivity`, `AssistantStatusSnapshot`, or a reimplementation of `foldAssistantEvents` exists.

#### US-108: `[enabler]` Enforce lint rules and bundle budgets in the build

- **Story**: Make the safety and size invariants build failures rather than review comments. Unblocks the security criterion in US-601 and FR-46.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given a `bypassSecurityTrustHtml` call anywhere outside `core/markdown/markdown.service.ts`, when lint runs, then it fails with the `no-restricted-syntax` rule.
  - Given `angular-eslint` and `@ngrx/eslint-plugin`, when lint runs, then `prefer-protected-state`, `with-state-no-arrays-at-root-level`, `enforce-type-call`, and `signal-store-feature-should-use-generic-type` are all active and passing.
  - Given a build where the diagram or math library has entered the initial chunk, when budgets are evaluated, then the build fails.
  - Given the workspace, when scripts are inspected, then `lint`, `format`, `test`, and `build` all exist and run clean.

### EP-2: Sign-in and session

#### US-201: Sign in with Entra ID

- **Story**: As an employee, I want to sign in with my work account so that I can use the assistant without a separate credential.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-102
- **Acceptance criteria**:
  - Given an unauthenticated visitor, when they request any route other than `/auth` or `/login-failed`, then they are redirected to Entra ID and no application chunk renders first.
  - Given a completed redirect, when `/auth` handles it, then the active account is selected, `TokenService` can produce a token, and the user lands on the chat route.
  - Given a failed or cancelled sign-in, when the redirect returns, then `/login-failed` renders with a retry control and no console error is the only feedback.
  - Given a signed-in user, when a request is issued through either `HttpClient` or the streaming `fetch`, then both carry a token obtained from the same `TokenService`.

#### US-202: Bootstrap the session from the current user

- **Story**: As a signed-in user, I want the app to know my identity, permissions, models, and MCP servers before it renders so that no screen renders an affordance I cannot use.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-201, US-104
- **Acceptance criteria**:
  - Given a completed sign-in, when `sessionGuard` resolves, then `POST /api/users/me` has returned and `SessionStore` holds the user and their permission set before any child route activates.
  - Given a resolved session, when the shell renders, then models, permitted MCP servers, conversations, and projects have all been requested, each sequenced after the bootstrap call rather than in parallel with it.
  - Given `POST /api/users/me` returning a non-2xx, when the guard runs, then the app renders an error state naming the `traceId` and does not render an empty shell.
  - Given a user with zero permission grants, when the session resolves, then the empty set is treated as a valid result and not as a load failure.

#### US-203: Keep the admin area off non-admin devices

- **Story**: As a security reviewer, I want the admin code never to reach a non-administrator's browser so that the admin surface is not merely hidden.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-202
- **Acceptance criteria**:
  - Given a user without the `Administrator` permission, when they navigate to `/admin`, then the guard runs on `canMatch`, the admin chunk is not requested over the network, and they land on the forbidden page.
  - Given a user without the permission, when the shell renders, then no admin navigation entry is displayed.
  - Given an administrator, when they navigate to `/admin`, then the chunk loads and the users tab renders.

#### US-204: Gate upload affordances on the Upload File permission

- **Story**: As a user without upload rights, I want the app not to offer me an upload control so that I am not shown a 403 after choosing a file.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-202
- **Acceptance criteria**:
  - Given a user whose permission set omits `b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e`, when the composer and the project files panel render, then no attach control, paperclip button, or drop zone is present.
  - Given an administrator whose permission set omits `Upload File`, when the composer renders, then the upload affordance is still absent — the check is on `Upload File` alone and never inferred from `Administrator`.
  - Given a user who does hold the permission, when the composer renders, then the attach control and the drop zone are both available.

#### US-205: Sign out and leave nothing behind

- **Story**: As a user on a shared machine, I want signing out to clear my data from the browser so that the next person sees nothing of mine.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-202, US-104
- **Acceptance criteria**:
  - Given a signed-in session with loaded conversations, projects, and an open transcript, when the user signs out, then `sessionEvents.signedOut` fires and every store composing `withResetOnSignOut` returns to its initial state.
  - Given sign-out, when `localStorage` is inspected, then theme and sidebar preferences remain and no conversation, project, document, or user data is present.
  - Given an in-flight turn, when the user signs out, then the request is aborted and no orphaned reader continues writing to a store.

### EP-3: Navigation and conversation sidebar

#### US-301: Collapse the sidebar and have it stay collapsed

- **Story**: As a user, I want to collapse the sidebar and find it still collapsed next time so that my layout preference persists.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-106
- **Acceptance criteria**:
  - Given an expanded sidebar at 260px, when the in-sidebar collapse control is used, then it animates to a 60px icon strip over 200ms and the main content reflows.
  - Given a collapsed sidebar, when the page is reloaded, then it is still collapsed, restored from `localStorage`.
  - Given the collapsed strip, when a user hovers an icon button, then a tooltip shows the full label, and each button is a 44px square target.

#### US-302: See my recent conversations

- **Story**: As a user, I want my conversations listed in the sidebar so that I can return to one in a click.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-202, US-104
- **Acceptance criteria**:
  - Given a signed-in user, when the shell renders, then `GET /api/conversations/search` is called with `name=` omitted and the returned items render newest-first in server order.
  - Given a user with no conversations, when the sidebar renders, then a designed empty state shows, distinct from the loading skeleton.
  - Given a conversation is open, when the sidebar renders, then its row is visually selected.
  - Given the list overflows, when the user scrolls it, then it scrolls independently of the rest of the sidebar.
  - Given the search field, when the user types, then the request sends `name=` — not `filter=` — and the list narrows.

#### US-303: Start a new conversation without creating one

- **Story**: As a user, I want "New conversation" to give me an empty prompt box so that abandoning the idea does not litter my sidebar with empty conversations.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-302
- **Acceptance criteria**:
  - Given any screen, when the user selects "New conversation", then the chat route renders an empty composer and no `POST /api/conversations` request is issued.
  - Given that empty state, when the user navigates away without sending, then no conversation exists in the sidebar after a reload.
  - Given the empty state, when it renders, then the centered wordmark, the "How can I help you today?" subtitle, and the suggested prompt chips are shown.

#### US-304: Rename a conversation without unlinking it

- **Story**: As a user, I want to rename a conversation so that I can find it later — and I want the rename to leave its project membership alone.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-302, US-106
- **Acceptance criteria**:
  - Given a conversation in a project, when the user renames it, then the `PUT /api/conversations` body echoes the current `projectId`, and after a refetch the conversation is still in that project.
  - Given the rename modal, when it opens, then it carries a text input, a character counter against the 256-character server limit, and Cancel and Rename buttons — no `prompt()` dialog.
  - Given a name longer than 256 characters or empty, when Rename is pressed, then the server's validation message renders against the field and nothing is saved.
  - Given a successful rename, when the response is pending, then the new name shows optimistically and rolls back if the request fails.

#### US-305: Favorite a conversation

- **Story**: As a user, I want to mark a conversation as a favorite so that I can filter to the ones I return to.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-302
- **Acceptance criteria**:
  - Given a conversation, when the user toggles favorite, then `PUT /api/conversations/{id}/favorite` is called and the row updates optimistically.
  - Given the optimistic patch, when it is applied, then `dateModified` is left untouched, because favoriting deliberately does not bump it server-side and a local bump would diverge on the next fetch.
  - Given a failed favorite call, when the error returns, then the toggle rolls back and an error toast names the `traceId`.

#### US-306: Delete a conversation

- **Story**: As a user, I want to delete a conversation I no longer need so that my sidebar stays relevant.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-302, US-106
- **Acceptance criteria**:
  - Given a conversation, when the user selects Delete, then a confirmation modal names it and offers a destructive red Delete action.
  - Given confirmation, when `DELETE /api/conversations/{id}` succeeds, then the row is removed optimistically and a success toast is raised.
  - Given the deleted conversation was the one open, when the delete completes, then the app navigates to the empty chat state rather than leaving a dead transcript on screen.
  - Given the request fails, when the error returns, then the row is restored and an error toast is raised.

#### US-307: Move a conversation into or out of a project

- **Story**: As a user, I want to assign a conversation to a project so that related work sits together.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-304, US-901
- **Acceptance criteria**:
  - Given a standalone conversation, when the user assigns it to a project, then the `PUT /api/conversations` body carries the target `projectId` together with the unchanged current name.
  - Given a conversation in a project, when the user unassigns it, then `projectId` is sent as null and the conversation appears as standalone after a refetch.
  - Given a conversation in project A, when the user moves it to project B, then it leaves A and appears under B without a second round trip.
  - Given every update path in the app, when the request body is built, then it goes through one `toUpdateBody()` helper, so no caller can omit `projectId` by accident.

### EP-4: Chat composition and turn execution

#### US-401: Send a first prompt and have the conversation created around it

- **Story**: As a user, I want typing and sending to start the conversation so that I never have to create one first.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-303, US-405
- **Acceptance criteria**:
  - Given the empty chat state, when the user sends their first prompt, then `POST /api/conversations` runs, then the stream request is issued against the new id, and the answer begins rendering.
  - Given that sequence, when the URL updates to include the new conversation id, then the page does not remount — one `UrlMatcher` serves both `/chat` and `/chat/:conversationId` and `Location.replaceState` performs the update.
  - Given a send with empty text and no attachments, when the send control is evaluated, then it is disabled.
  - Given the conversation creation fails, when the error returns, then the prompt text stays in the composer and is not lost.

#### US-402: Choose the model for a turn

- **Story**: As a user, I want to pick which model answers so that I can trade cost against capability per question.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-202
- **Acceptance criteria**:
  - Given a loaded model catalog from `GET /api/models`, when the composer renders, then the model marked `isDefault` is preselected.
  - Given the picker, when the user changes model, then the selection persists for subsequent turns in that session.
  - Given the model catalog request fails, when the composer renders, then the send control is disabled with an explanatory message rather than sending without a `modelId`.

#### US-403: Select MCP servers, and be stopped from selecting them where they cannot work

- **Story**: As a user, I want to enable tool servers for a turn and be prevented from doing so on a model that cannot call tools, so that I do not get an opaque 400.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-402
- **Acceptance criteria**:
  - Given `GET /api/mcps`, when the picker renders, then only the servers the caller is permitted appear, each toggling independently with the dropdown staying open, and the button label reads "Tools", "1 Tool", or "N Tools".
  - Given a selected model whose `isToolEnabled` is false, when the picker renders, then it is disabled with a reason, any prior selection is cleared, and the stream request carries an empty `mcpServers` array.
  - Given selected servers, when the turn is sent, then `mcpServers` carries `{ id }` for each — the `mcpServerIds` field is not dropped, which the old client did.
  - Given a 502 `/problems/mcp-server-unavailable`, when it returns before the first frame, then the error names the `serverName` extension.

#### US-404: `[enabler]` Decode the SSE frame stream

- **Story**: Parse the wire format into `AssistantUiEvent` values, correctly across chunk boundaries. Unblocks US-406 and all of EP-5.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-107
- **Acceptance criteria**:
  - Given a byte stream of `data: {json}\n\n` frames, when decoded, then one `AssistantUiEvent` is emitted per frame, in order, with the `data: ` prefix stripped.
  - Given a chunk boundary that splits a frame mid-JSON, when the next chunk arrives, then the partial-line buffer is carried across reads and the frame is parsed once, intact — verified by a fixture that deliberately splits one frame across two reads.
  - Given a frame whose `kind` is not one of the eight known values, when decoded, then it is ignored rather than thrown on, so a server-side protocol addition does not break the client.
  - Given `TextDecoder`, when it is constructed, then `{ stream: true }` is set so multi-byte characters spanning a chunk boundary are not corrupted.
  - Given a raw-text fallback codec, when the config flag selects it, then unframed bytes render as plain answer text.

#### US-405: `[enabler]` Stream over an abortable fetch that reads errors first

- **Story**: Provide the transport that acquires a token, checks the response before reading any body bytes, and can be aborted. Unblocks US-401, US-406, and US-407.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103, US-404
- **Acceptance criteria**:
  - Given a stream request, when the response returns, then `response.ok` is checked and any problem body is parsed **before** a single byte of the body is read — headers are set lazily on the first frame, so 400, 403, 404, 409, 502, and 503 all arrive as ordinary `application/problem+json`.
  - Given a token requirement, when the request is built, then the bearer token comes from the same `TokenService` the `HttpClient` interceptor uses.
  - Given an `AbortController`, when `abort()` is called, then the reader stops, the request is cancelled, and no further events are emitted — there is no stop endpoint and none is called.
  - Given a running stream, when events are emitted, then they are buffered on a ~16ms budget and folded as one batch, producing at most one `patchState` per frame at any token rate.
  - Given a synthetic `ReadableStream` of frames in a Node test, when the transport reads it, then it completes with no browser and no backend.

#### US-406: Watch the answer arrive

- **Story**: As a user, I want to see the answer appear as it is generated so that I can start reading before it finishes.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-405, US-104
- **Acceptance criteria**:
  - Given a turn in flight, when `TextDelta` events arrive, then the accumulating text renders progressively and is held in one string on `TurnStore`, never appended to the transcript array per chunk.
  - Given the turn completes, when `Finished` arrives, then exactly one append to the transcript occurs and the streaming buffer is cleared.
  - Given the window between request-accepted and the first `ActivityStarted` or `TextDelta`, when it is rendered, then a thinking indicator shows — this application emits no request-level `Status` events, so no status line is fabricated to fill it.
  - Given the response body ends with no `Finished` event, when the reader completes, then the turn renders as "This response was cut off" with a Retry control, because after the first frame there is no error surface and a faulted turn is indistinguishable from a network drop.
  - Given a turn is in flight, when the transcript container is inspected, then `aria-busy` is set on it.

#### US-407: Stop a turn and keep what it produced

- **Story**: As a user, I want to stop a long answer and still keep the text I got, clearly marked as not saved, so that I am never misled about what the model will see next turn.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-406
- **Acceptance criteria**:
  - Given a turn in flight, when the user presses Stop, then the fetch is aborted and the send control returns within 200ms.
  - Given the stop, when the UI settles, then the partial answer renders in a visually detached card reading "Stopped — not saved to this conversation", carrying Copy and Retry, and it is **not** appended to the transcript.
  - Given the stop, when the UI settles, then the optimistic user bubble is also removed, because a cancelled turn transcribes neither the answer nor the prompt.
  - Given the detached card, when the user reopens the conversation, changes route, or refreshes, then it is gone.
  - Given Retry, when pressed, then the original prompt is restored into the composer with its model and MCP selection intact.

#### US-408: Recover when the conversation is already busy

- **Story**: As a user with two tabs open, I want a clear message when a turn is already running so that I do not think the app is broken.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-405
- **Acceptance criteria**:
  - Given a turn already in flight for a conversation, when a second send is issued, then the 409 `/problems/conversation-busy` renders as a transient warning naming the conflict with a retry affordance.
  - Given that 409, when it is handled, then no automatic retry is scheduled at any interval.
  - Given the warning, when the user retries manually and the first turn has ended, then the second turn starts normally.

#### US-409: See the name the server gave my conversation

- **Story**: As a user, I want my new conversation to acquire a meaningful title on its own so that I can recognise it in the sidebar.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-406, US-302
- **Acceptance criteria**:
  - Given the first turn of a new conversation completes, when `turnEvents.completed` fires with `wasFirstTurn`, then `GET /api/conversations/{id}` is refetched and the sidebar row shows the generated name without a manual refresh.
  - Given a subsequent turn, when it completes, then no title refetch is issued.
  - Given a first turn that was stopped or truncated, when the user retries, then the title refetch happens after the turn that actually completes, not after the abandoned one.

#### US-410: Resume a conversation with the settings it last used

- **Story**: As a user, I want reopening a conversation to restore its model and tool selection so that the next turn behaves like the last one.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-402, US-403
- **Acceptance criteria**:
  - Given a conversation with a previous turn, when it is opened, then `GET /api/conversations/{id}` supplies `modelId` and `mcpServerIds`, and both pickers reflect them.
  - Given a conversation whose stored model has since been deactivated, when it is opened, then the picker falls back to the default model and states that the previous model is unavailable.
  - Given a refetched transcript that is shorter than the locally held one — which happens after an aborted turn — when the conversation is opened, then the local transcript is **replaced**, not merged.

#### US-411: `[enabler]` Carry the consent scope on the MCP authorization problem (B9)

- **Story**: Add the `scope` the caller must consent to onto the `/problems/mcp-authorization-required` body, so a client can offer an interactive consent action instead of a dead end. `McpDto` deliberately omits `scope` and only the administrative `McpServerDto` carries it, so the client cannot derive it. Unblocks US-412.
- **Priority**: P2 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given an MCP server requiring interactive consent, when the turn returns 403, then the problem body carries a `scope` extension alongside the existing `serverName`, and the response is still a 403 with the same `type` URI.
  - Given a server whose auth type requires no scope, when the 403 is produced, then `scope` is omitted rather than sent empty.
  - Given the change, when existing clients are considered, then it is additive only — no existing extension is renamed or removed.

#### US-412: Understand an MCP consent requirement

- **Story**: As a user whose selected tool server needs consent, I want to be told what is required so that I am not stuck in a silent failure.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-403, US-103
- **Acceptance criteria**:
  - Given a 403 `/problems/mcp-authorization-required`, when it is handled, then no token refresh is attempted and no refresh loop occurs.
  - Given that error, when it renders, then it names the `serverName` extension and explains that the server needs interactive authorization.
  - Given US-411 has not landed, when the error renders, then it shows the explanation with no consent button — the interim behavior, because the scope needed to request consent is not on the wire.
  - Given US-411 has landed, when the error renders, then a consent action using the supplied `scope` is offered.

#### US-413: Dictate a prompt

- **Story**: As a user, I want to speak my prompt so that I can compose hands-free.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-401
- **Acceptance criteria**:
  - Given a browser supporting speech recognition, when the user activates the voice control, then transcribed text is inserted into the composer and the control shows an active recording state.
  - Given a browser without support, when the composer renders, then the voice control is absent rather than present-and-broken.
  - Given the user denies microphone permission, when recording is attempted, then a message explains the denial and the composer stays usable.
  - Given a turn is streaming, when the composer renders, then the voice control is disabled alongside send.

### EP-5: Assistant activity timeline

#### US-501: See what the assistant is doing, in the order it happened

- **Story**: As a user, I want each tool call, MCP call, and sub-agent call shown as its own card in chronological order alongside the answer text so that I can follow the assistant's work as it happens.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-404, US-406
- **Acceptance criteria**:
  - Given a turn emitting interleaved `TextDelta` and `ActivityStarted` events, when the timeline renders, then text blocks and activity cards appear in the order the events arrived, each activity shown separately even though the underlying model is hierarchical.
  - Given an activity card, when it renders, then `displayName` is the label and `kind` is a **separate** badge; a composed string such as "Calling Weather MCP" fails this criterion.
  - Given an activity, when it has a `source`, then that renders as a subtitle; when it has `subStatuses`, they render as a nested list beneath the card.
  - Given `state`, when it is `Running`, `Completed`, or `Failed`, then a spinner, a check, or a warning renders respectively, with `durationSeconds` shown on completion.
  - Given the activity state, when it is computed, then it comes from the vendored `foldAssistantEvents` and the ordering index is a thin separate structure — neither duplicates the other.
  - Given a recorded fixture of a full turn, when it is folded in a Vitest spec, then the resulting ordering matches the recorded arrival order exactly.

#### US-502: See nested work inside the activity that caused it

- **Story**: As a user, I want an agent's own tool calls shown inside that agent's card so that I can tell nested work from top-level work.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-501
- **Acceptance criteria**:
  - Given an `ActivityStarted` carrying a `parentScopeId`, when it is folded, then it renders nested inside the matching parent card and does **not** push a new top-level timeline node.
  - Given an `ActivityStarted` with no `parentScopeId`, when it is folded, then it becomes a new top-level card.
  - Given a child whose `parentScopeId` matches no known scope, when it is folded, then it is placed at the top level rather than dropped.
  - Given nesting three levels deep, when it renders, then indentation reflects depth and remains legible at the <768px breakpoint.

#### US-503: Read the model's reasoning as it streams

- **Story**: As a user, I want to see the model's reasoning summary so that I can judge how it reached the answer.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-501
- **Acceptance criteria**:
  - Given `ReasoningDelta` events, when they arrive, then the reasoning text accumulates in its own region, visually distinct from the answer text.
  - Given a turn with reasoning, when the region renders, then it is collapsible and its expanded state persists for the duration of the turn.
  - Given a turn with no `ReasoningDelta` events, when it renders, then no empty reasoning region appears.

#### US-504: See what a turn cost

- **Story**: As a user, I want the token count for a completed turn so that I understand my usage without leaving the chat.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-501
- **Acceptance criteria**:
  - Given a `Finished` event carrying `usage`, when the turn completes, then input, output, and total token counts render on the completed turn.
  - Given a `Finished` event whose `usage` reports only some counts, when it renders, then only the reported counts are shown — an absent count is not rendered as zero.
  - Given an activity carrying its own `usage`, when its card is expanded, then that activity's token split is shown.
  - Given a stopped or truncated turn, when it settles, then no token counts are claimed, because `Finished` never arrived.

#### US-505: Understand an activity that failed

- **Story**: As a user, I want a failed tool call to say so on its card so that a partial answer is explicable.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-501
- **Acceptance criteria**:
  - Given an `ActivityFailed` event, when it is folded, then the card's state becomes `Failed`, a warning indicator renders, and `durationSeconds` is shown.
  - Given a failed child activity, when it renders, then the parent card's own state is not silently marked failed unless the parent also failed.
  - Given a turn where an activity failed but the answer completed, when the turn ends, then the answer renders normally with the failed card retained in place.

### EP-6: Answer rendering and reading

#### US-601: Render model output as markdown that cannot execute

- **Story**: As a security reviewer, I want model-authored markdown sanitized before it reaches the DOM so that an uploaded document or an MCP tool result cannot inject script into the client.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-108
- **Acceptance criteria**:
  - Given a response containing `<img src=x onerror=alert(1)>`, `<script>alert(1)</script>`, and an anchor with a `javascript:` href, when it renders, then no script executes, the payload appears as inert text or is stripped, and a unit test asserts each case.
  - Given `MarkdownService`, when inspected, then `markdown-it` is configured with `html: false`, the output passes through DOMPurify with an explicit profile, and this file holds the only `bypassSecurityTrustHtml` call in the app.
  - Given any store, when its state is inspected, then it holds markdown source text and never a `SafeHtml` value.
  - Given a lint run, when a second `bypassSecurityTrustHtml` is introduced anywhere, then the build fails.

#### US-602: Read a streaming answer without the page slowing down

- **Story**: As a user reading a long answer, I want rendering to stay smooth to the end so that a long response does not degrade as it grows.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-601, US-406
- **Acceptance criteria**:
  - Given accumulated text, when it is split for rendering, then the split point is the last block boundary (`lastIndexOf('\n\n')`), except inside an unclosed fence — an odd count of line-start triple backticks — where the fence open is the split point.
  - Given the head, when it is rendered, then it is memoized by head length and re-rendered only when the head grows; only the tail is re-rendered per flush.
  - Given the tail, when it is rendered mid-stream, then syntax highlighting is skipped because it may be a partial code block.
  - Given `Finished`, when it arrives, then the whole buffer is rendered once, canonically, with highlighting — and a unit test asserts that this canonical render is character-identical to a single full render of the same source.

#### US-603: Copy a code block

- **Story**: As a user, I want a copy button on each code block so that I can take code without selecting it by hand.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-601
- **Acceptance criteria**:
  - Given a rendered code block, when it appears, then the copy affordance is emitted by a custom fence renderer as part of the HTML — not grafted onto the DOM after render.
  - Given a click on any copy control in the transcript, when it is handled, then one delegated listener on the transcript container handles it, not a listener per `<pre>`.
  - Given a copy, when it succeeds, then the exact block source is on the clipboard and a confirmation state shows for 2 seconds.

#### US-604: Read code in a theme that matches the page

- **Story**: As a dark-mode user, I want syntax highlighting that is legible in dark mode so that code blocks are not a white rectangle.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-105, US-603
- **Acceptance criteria**:
  - Given a theme change, when it is applied, then the highlight.js stylesheet swaps between a light and a dark variant.
  - Given a dark theme on first load, when the first code block paints, then it is already using the dark stylesheet — no light-styled frame is painted.
  - Given both themes, when a code block is measured, then its foreground-to-background contrast meets WCAG 2.1 AA at 4.5:1 for normal text.

#### US-605: Render diagrams and math without paying for them upfront

- **Story**: As a user, I want diagrams and formulas to render when a response contains them, without every other page load carrying the cost.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-601, US-108
- **Acceptance criteria**:
  - Given a response containing a Mermaid fence or math notation, when the flag for that feature is enabled in `config.json`, then the library is loaded by dynamic import at that moment and the content renders.
  - Given a build, when the initial chunk is analysed, then neither the diagram nor the math library is present, and the budget check fails the build if either enters it.
  - Given the flag is disabled, when such content appears, then the source renders as a plain code block rather than an error.
  - Given a diagram that fails to parse, when rendering is attempted, then the raw source is shown with an inline notice, not a blank area.

#### US-606: Stay with the latest message, or stay where I am reading

- **Story**: As a user, I want the transcript to follow the newest content unless I have scrolled up to read, in which case I want a way back down.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-406
- **Acceptance criteria**:
  - Given a turn streaming and the user at the bottom, when new content arrives, then the transcript stays pinned to the newest content.
  - Given the user scrolls up during streaming, when new content arrives, then the view does not move and a circular jump-to-latest control appears centered above the composer.
  - Given the jump-to-latest control, when it is pressed, then the view scrolls to the bottom, the control hides, and pinning resumes.
  - Given a turn completes while the user is scrolled up, when `Finished` arrives, then the view does **not** jump to the bottom.
  - Given the implementation, when inspected, then bottom detection uses an `IntersectionObserver` on a zero-height sentinel with `rootMargin: '0px 0px 80px 0px'` rather than a `(scroll)` binding, and the container sets `overflow-anchor: none`.

#### US-607: Copy a prompt or a response

- **Story**: As a user, I want to copy what I asked or what I got so that I can paste it elsewhere.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-601
- **Acceptance criteria**:
  - Given a completed turn, when the user hovers the user bubble or the assistant message, then a copy control appears beneath it.
  - Given the assistant copy control, when it is used, then the original markdown source is copied, not the rendered HTML.
  - Given a copy, when it completes, then a confirmation state shows for 2 seconds and the control is reachable by keyboard.

### EP-7: Conversation library

#### US-701: Find a conversation by name

- **Story**: As a user with many conversations, I want to search them by name so that I can find one without scrolling.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-302
- **Acceptance criteria**:
  - Given the conversations screen, when the user types in the search field, then the request is debounced and sends `name=` — a request carrying `filter=` fails this criterion.
  - Given a search returning nothing, when results render, then a designed empty state names the search term.
  - Given a search term, when it is applied, then it is reflected in the URL query params so the filtered list is shareable and the back button restores the prior state.

#### US-702: Page through my conversations

- **Story**: As a user, I want to load more conversations beyond the first page so that older ones are reachable.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-701
- **Acceptance criteria**:
  - Given the first page, when it renders, then a counter above the list reads "Showing N of M conversations" from the `totalCount` in the envelope.
  - Given more results exist, when the user presses Load more, then the next page appends in server order and the counter updates.
  - Given every result is loaded, when the list renders, then the Load more control is absent rather than disabled.
  - Given `take`, when requests are built, then it never exceeds 100, because the server clamps it.

#### US-703: Filter to my favorites

- **Story**: As a user, I want to see only my favorite conversations so that I can reach the ones I use most.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-701, US-305
- **Acceptance criteria**:
  - Given the favorites filter, when enabled, then `isFavorite=true` is sent on the search request and the list narrows.
  - Given the filter is active and the user unfavorites a row, when the list refreshes, then that row leaves the filtered list.
  - Given the filter state, when it changes, then it is reflected in the URL query params.

#### US-704: Delete several conversations at once

- **Story**: As a user cleaning up, I want to select multiple conversations and delete them together so that I do not confirm one at a time.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-702, US-106
- **Acceptance criteria**:
  - Given the list, when any row checkbox is checked, then a floating bulk action bar shows "N selected" and a red Delete Selected action.
  - Given a bulk delete, when confirmed, then `DELETE /api/conversations/bulk` is called once with all ids and the rows are removed optimistically.
  - Given a bulk delete that fails, when the error returns, then every selected row is restored and one error toast is raised — not one per row.
  - Given the select-all control in the header, when used, then it selects only the rows currently loaded, and the bulk bar states how many that is.

#### US-705: Understand why conversations cannot be sorted

- **Story**: As a user, I want the app to be honest that it cannot reorder this list, rather than offering a sort that silently reorders one page.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-702
- **Acceptance criteria**:
  - Given the conversations screen, when it renders, then no sort control is offered — the list is in server order, and only `name` search and `isFavorite` filtering are exposed.
  - Given a partially loaded list, when the user has pressed Load more, then no client-side reordering has been applied to the combined set.
  - Given US-706 has landed, when the screen renders, then a sort control appears and issues `sort=` and `dir=` to the server.

#### US-706: `[enabler]` Accept sort parameters on the paginated list endpoints (B8)

- **Story**: Add `sort` and `dir` query parameters to conversation search, project search, and user search, so paginated lists can be ordered without the client draining every page first. No endpoint accepts a sort parameter today. Unblocks US-705, US-908, and honest sorting on admin users.
- **Priority**: P2 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given `GET /api/conversations/search`, `GET /api/projects`, and `GET /api/users`, when called with `sort` and `dir`, then results are ordered server-side and the `PaginatedResponseDto` envelope is unchanged.
  - Given an unrecognised `sort` value, when the request is handled, then it returns 400 as a validation problem naming the parameter, rather than silently ignoring it.
  - Given the parameters are omitted, when the request is handled, then the current default ordering is preserved byte-for-byte, so existing clients see no change.
  - Given the change, when integration tests run, then each endpoint has a test asserting order for at least one ascending and one descending case.

### EP-8: Attachments, upload, and download

#### US-801: Attach files to a conversation

- **Story**: As a user, I want to attach documents to my conversation so that the assistant can answer questions about them.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-204, US-401
- **Acceptance criteria**:
  - Given the composer, when the user uses the paperclip control or drops files onto the prompt box, then a chip per file shows an extension-appropriate icon, the truncated name, the human-readable size, and a remove control.
  - Given a drag over the prompt box, when it starts, then a dashed-border overlay reading "Drop file(s)" shows, and it clears on drop or drag-leave.
  - Given attached files, when the turn is sent, then each is posted to `POST /api/documents/conversations/{conversationId}` and a job id is returned per file.
  - Given `GET /api/documents/file-extensions`, when the picker opens, then it constrains selection to the supported list rather than accepting anything and failing server-side.
  - Given attachment state, when it is held, then chips are immutable records in `UploadStore` — a mutated plain object would not trigger rendering under zoneless change detection.

#### US-802: Watch an upload get processed

- **Story**: As a user, I want to see real progress while my document is ingested so that I know whether to wait.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-801
- **Acceptance criteria**:
  - Given a queued job, when polling starts, then `GET /api/documents/upload-status/{jobId}` is called on a 500ms → 1s → 2s → 4s schedule capped at 5s, not a fixed interval.
  - Given successive polls, when progress is applied, then the displayed value never decreases, and `message`, `completedUnits`, and `totalUnits` render when present.
  - Given `state` reaches `Succeeded` or `Failed`, when it is observed, then polling stops and the terminal state renders — `Failed` shows the job's `errorMessage`.
  - Given a job that succeeds, when it completes, then the returned `documentId` is retained so the document can be downloaded without a further lookup.

#### US-803: Tell an expired upload apart from a failed one

- **Story**: As a user returning to an old tab, I want an expired upload job to say it expired rather than claim my file failed.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-802
- **Acceptance criteria**:
  - Given a 404 from the upload-status route, when it is handled, then the job renders as expired or unknown with distinct wording and iconography from a `Failed` job.
  - Given an expired job, when it renders, then polling has stopped and the copy explains that status is retained for a limited window.
  - Given the same 404 shape returned for another user's job, when it is handled, then it renders identically to the expired case — the client must not distinguish them, because the server deliberately does not.

#### US-804: Download a document

- **Story**: As a user, I want to download a file I uploaded so that I can retrieve the original.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-802
- **Acceptance criteria**:
  - Given a document row, when the user clicks download, then the signed URL is requested at that moment and immediately used; a list rendering N documents issues zero download requests until a click.
  - Given a fetched `DocumentDownloadDto`, when the download proceeds, then `downloadUrl` is never written to `localStorage`, `sessionStorage`, a router URL, or any log, and the browser saves the file under the signed `Content-Disposition` name via an `<a download>`.
  - Given a 503 `/problems/storage-not-configured`, when it returns, then a message explains that downloads are unavailable in this deployment rather than showing a generic failure.
  - Given a 404, when it returns, then the row reports the document as unavailable without asserting it was deleted.

#### US-805: Understand why a file was rejected

- **Story**: As a user, I want to know why my file was not accepted so that I can fix it.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-801, US-103
- **Acceptance criteria**:
  - Given a 400 `/problems/upload-too-large`, when it returns, then the message states the limit using the `maxBytes` extension formatted as a human-readable size.
  - Given a 403 `/problems/permission-required`, when it returns, then the message names the missing permission from the `permissions[]` extension.
  - Given an unsupported extension, when the file is selected, then it is rejected client-side against the `file-extensions` list with the supported set named, before any upload is attempted.

### EP-9: Projects

#### US-901: Browse and search projects

- **Story**: As a user, I want a projects screen listing my projects with a name search so that I can find one quickly.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-104, US-106
- **Acceptance criteria**:
  - Given the projects route, when it loads, then `GET /api/projects` is called and projects render as cards or rows showing name, description, and last-modified.
  - Given the search field, when the user types, then `name=` is sent debounced and the URL query params reflect the term.
  - Given a user with no projects, when the screen renders, then a designed empty state with a create action shows.
  - Given the list, when it loads, then it drains at `take=100` up to a 500-item ceiling so client-side sorting is offered over a fully materialised set.

#### US-902: Sort projects, honestly

- **Story**: As a user, I want to sort projects by last updated, created, or alphabetically, and to be told when the sort covers only part of my projects.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-901
- **Acceptance criteria**:
  - Given fewer than 500 projects, when a sort is chosen, then the full set is reordered and the choice is reflected in the URL query params.
  - Given more than 500 projects, when the list hits the ceiling, then sorting is disabled with a visible reason and the counter reads "Showing the 500 most recent of N".
  - Given the pinned sort option, when US-908 has not landed, then it orders by the device-local pins and is labelled as device-local; when US-908 has landed, it orders by the server flag.

#### US-903: Create, rename, describe, and delete a project

- **Story**: As a user, I want full lifecycle control of a project so that my grouping stays accurate over time.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-901
- **Acceptance criteria**:
  - Given the create action, when a name is submitted, then `POST /api/projects` runs, the project appears, and the operation is not applied optimistically because it creates a server id.
  - Given a duplicate name, when submitted, then the server's validation message renders against the name field and nothing is created.
  - Given a rename or description edit, when `PUT /api/projects/{id}` is built, then it carries both `description` and `instructions` — this is a full-representation PUT and omitting a field clears it.
  - Given delete, when confirmed in a modal naming the project, then `projectEvents.deleted(id)` fires and the project list removes it, the conversation list releases its conversations as standalone, the active project navigates away, and the project documents panel clears.

#### US-904: Give a project standing instructions

- **Story**: As a user, I want instructions that apply to every conversation in a project so that I do not repeat context in each chat.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-903
- **Acceptance criteria**:
  - Given the project detail screen, when instructions are edited and saved, then `PUT /api/projects/{id}` carries the new instructions along with the current name and description.
  - Given instructions, when they are saved, then the change is **not** applied optimistically — the saved state renders only after the server confirms.
  - Given unsaved edits, when the user navigates away, then they are warned before the change is discarded.
  - Given `GET /api/projects/{id}`, when the detail loads, then instructions come from `ProjectDto` — the listing shape omits them by design.

#### US-905: Manage a project's files

- **Story**: As a user, I want to add and remove files on a project so that its conversations share the same reference documents.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-802, US-903
- **Acceptance criteria**:
  - Given the project files panel, when a file is added, then `POST /api/documents/projects/{projectId}` runs and the same staged polling and progress rules as US-802 apply.
  - Given `GET /api/projects/{id}/documents`, when the panel loads, then each row shows name, extension, human-readable size, and created date from `ProjectDocumentDto`.
  - Given a file removal, when confirmed, then `DELETE /api/projects/{projectId}/documents/{documentId}` runs and the row is removed optimistically with rollback on failure.
  - Given the user lacks `Upload File`, when the panel renders, then the add control is absent and existing files remain listed and downloadable.

#### US-906: Start a conversation inside a project

- **Story**: As a user, I want to type a prompt on the project screen and have the conversation created inside that project so that it inherits the instructions and files.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-401, US-903
- **Acceptance criteria**:
  - Given the project detail composer, when a prompt is sent, then `POST /api/conversations` carries that `projectId` and the turn streams as it does on the chat route.
  - Given the new conversation, when it is created, then the app navigates to the chat route for it and the project's linked conversation list includes it on return.
  - Given a project that has been deleted in another tab, when a prompt is sent, then the 404 renders as "this project no longer exists" and the user is returned to the projects list.

#### US-907: `[enabler]` Filter conversation search by project (B1)

- **Story**: Add a `projectId` filter to conversation search, or a `GET /api/projects/{id}/conversations` route. `SearchConversationsAsync(name, skip, take, isFavorite)` accepts no project filter today, so the client can only get a project's conversations by draining the whole list. Unblocks US-908 and the sidebar tree in US-910.
- **Priority**: P1 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given a `projectId`, when conversation search is called with it, then only that project's active conversations are returned, in the same `PaginatedResponseDto` envelope, scoped to the caller.
  - Given a `projectId` belonging to another user or a deactivated project, when the search runs, then it returns 404 in the same way the single-project route does, so the API cannot be used to probe for project ids.
  - Given both `projectId` and `name`, when supplied together, then both filters apply.
  - Given `projectId` is omitted, when the search runs, then behavior is byte-for-byte unchanged from today.

#### US-908: See and manage a project's conversations

- **Story**: As a user, I want a project's conversations listed on its detail screen with the same actions I have elsewhere so that I can manage them in context.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-903, US-307
- **Acceptance criteria**:
  - Given US-907 has not landed, when the panel loads, then the client drains conversation search at `take=100` to the 500-item ceiling and filters by `projectId` locally, and if the ceiling is reached the panel states "Showing the 500 most recent conversations" — this is the interim behavior and must be visible, not silent.
  - Given US-907 has landed, when the panel loads, then it issues one filtered request and the ceiling notice is gone.
  - Given a listed conversation, when its row menu is opened, then favorite, rename, delete, remove-from-project, and move-to-another-project are all offered.
  - Given remove-from-project or move, when either is used, then the update goes through `toUpdateBody()` so the name is echoed and only `projectId` changes.
  - Given the panel is empty, when it renders, then it shows an empty state with the project composer as the call to action.

#### US-909: `[enabler]` Support project favorites (B3)

- **Story**: Add a per-user favorite flag to projects, mirroring the conversation favorite design that already works — a dedicated route plus a field on the listing shape. `ProjectSummaryDto` carries no favorite or pin concept today. Unblocks US-910 and the pinned sort in US-902.
- **Priority**: P2 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given `PUT /api/projects/{id}/favorite` with `{ isFavorite }`, when called by the owner, then the flag is persisted and 204 is returned, matching the conversation route's shape and status.
  - Given `ProjectSummaryDto`, when returned by any project route, then it carries `isFavorite`.
  - Given the favorite is set, when it is persisted, then `DateModified` is **not** bumped, matching the conversation behavior so client-side optimistic patches stay consistent across both entities.
  - Given `GET /api/projects`, when called with `isFavorite=true`, then only favorited projects are returned.

#### US-910: Pin favorite projects to the sidebar

- **Story**: As a user, I want my favorite projects in the sidebar with their conversations beneath them so that my active work is one click away.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-909, US-907, US-301
- **Acceptance criteria**:
  - Given US-909 has not landed, when the sidebar renders, then favorites come from device-local pins in `localStorage` via `UiStore`, and the section header states that pins are device-local and do not sync.
  - Given US-909 has landed, when the sidebar renders, then favorites come from the server flag and the device-local notice is gone.
  - Given a favorited project, when its sidebar node is expanded, then its conversations render beneath it, sourced from US-907's filtered endpoint where available and from the drained-and-filtered set otherwise.
  - Given a project row in the sidebar, when its menu is opened, then unfavorite, rename, and delete are offered.
  - Given the sidebar is collapsed to the 60px strip, when it renders, then the project tree is replaced by an icon entry with a tooltip.

### EP-10: Documents library

#### US-1001: `[enabler]` List the documents belonging to a conversation (B2)

- **Story**: Expose the documents uploaded into a conversation. `ConversationDocumentDto` exists and `CosmosConversation.Documents` is defined, but no endpoint returns them and the collection is never populated — so the client can upload a document and later download it by id, but can never enumerate what a conversation holds. This blocks the entire Documents screen. Unblocks US-1002 and US-1003.
- **Priority**: P1 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given `GET /api/conversations/{id}/documents`, when called by the owner, then it returns the conversation's active documents with at minimum id, name, extension, mime type, size, and created date — matching the field set `ProjectDocumentDto` already provides, so both listings key the same way.
  - Given a conversation belonging to another user or deactivated, when the route is called, then it returns 404, consistent with the rest of the conversation group.
  - Given a conversation with no documents, when the route is called, then it returns an empty list with 200, not 404.
  - Given the Documents screen needs every document across every conversation, when this contract is designed, then it is decided and recorded whether the screen fans out one request per conversation or whether a cross-conversation listing is added instead — the per-conversation route alone forces an N+1 over the conversation list, which is acceptable for the grouped view but not for an unbounded library.

#### US-1002: Browse every document I have uploaded

- **Story**: As a user, I want one screen listing the documents I have uploaded across all my conversations so that I can find a file without remembering which chat it was in.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-1001, US-106
- **Acceptance criteria**:
  - Given US-1001 has not landed, when the documents route is opened, then `UnavailablePanelComponent` renders naming the missing capability, and `DocumentLibraryStore` holds zero records — a mock or seeded row fails this criterion.
  - Given US-1001 has landed, when the route is opened, then documents render with name, size, extension, created date, and the conversation they belong to.
  - Given a document row, when download is used, then US-804's rules apply — the signed URL is fetched on click only.
  - Given the fetch fails, when the error renders, then the screen shows an error state with the `traceId` and a retry control, distinct from the unavailable state.

#### US-1003: Filter documents and group them by conversation

- **Story**: As a user with many documents, I want to filter by name and group by conversation so that I can narrow a long list.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-1002
- **Acceptance criteria**:
  - Given the documents screen, when a name filter is typed, then the list narrows client-side and the term is reflected in the URL query params.
  - Given the group-by-conversation toggle, when enabled, then documents are grouped under collapsible conversation headings showing a per-group count.
  - Given a filter matching nothing, when applied, then an empty state names the filter term and offers to clear it.

#### US-1004: `[enabler]` Distinguish model-created documents (B7)

- **Story**: Introduce a concept for documents the assistant produced, as distinct from documents a user uploaded. No such concept exists anywhere in the data model today. Unblocks US-1005.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-1001
- **Acceptance criteria**:
  - Given a document record, when returned by any document listing, then it carries an origin discriminator distinguishing a user upload from a model-created artifact.
  - Given existing documents, when the discriminator is introduced, then all of them default to the user-upload value and no existing listing response changes shape for a client that ignores the new field.
  - Given the discriminator, when a listing is filtered by it, then only matching documents are returned.

#### US-1005: Filter to documents the assistant created

- **Story**: As a user, I want to see only the files the assistant produced so that I can separate its output from my inputs.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-1004, US-1002
- **Acceptance criteria**:
  - Given US-1004 has not landed, when the documents screen renders, then the LLM-created filter is **hidden** — not shown-and-disabled, and not shown returning zero results.
  - Given US-1004 has landed, when the filter is enabled, then only model-created documents render and the filter state is in the URL query params.
  - Given the filter is on and nothing matches, when the list renders, then an empty state explains that the assistant has not created any documents yet.

### EP-11: Response feedback

#### US-1101: `[enabler]` Put a message identity on the transcript and the stream (B4)

- **Story**: Add an id to transcript messages and to the turn's completion signal. `ConversationMessageDto` is `{ text, role }` only, so the client has no stable anchor for any per-message action — yet the server already mints these ids in `PersistTurnAsync` and correlates them on the usage row's `AssistantMessageId`. Unblocks US-1102 and US-1103, and removes the composite-key workaround.
- **Priority**: P1 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given `GET /api/conversations/{id}/messages`, when it returns, then each message carries `id` and `dateCreated`, and assistant messages carry their `usage`.
  - Given a completed turn, when the `Finished` event is emitted, then it carries `assistantMessageId` matching the id the transcript will return for that message.
  - Given a cancelled or failed turn, when it ends, then no `assistantMessageId` is claimed, consistent with the usage row leaving it null.
  - Given the change, when a client that ignores the new fields consumes either response, then its behavior is unchanged.

#### US-1102: `[enabler]` Record feedback on an assistant message (B5)

- **Story**: Add the endpoint that stores a thumbs up or thumbs down against an assistant message. None exists. Unblocks US-1103.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-1101
- **Acceptance criteria**:
  - Given `POST /api/conversations/{id}/messages/{messageId}/feedback` with a rating, when called by the conversation's owner, then the rating is persisted and re-submitting replaces the prior value rather than creating a duplicate.
  - Given a message id that does not belong to the conversation, or a conversation not owned by the caller, when the route is called, then it returns 404 consistently with the rest of the group.
  - Given a rating on a user message rather than an assistant message, when submitted, then it returns 400 as a validation problem.
  - Given the transcript response, when it is returned, then any existing rating is included so the UI can render the current state.

#### US-1103: Rate an assistant response

- **Story**: As a user, I want to mark a response as helpful or unhelpful so that the platform learns which answers work.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-1102, US-607
- **Acceptance criteria**:
  - Given US-1101 and US-1102 have not landed, when a completed assistant message renders, then the thumbs controls are **absent** — the interim behavior is to ship no feedback affordance at all, because there is no stable anchor and nowhere to send a rating.
  - Given both enablers have landed, when the user hovers a completed assistant message, then thumbs up and thumbs down appear beside the copy control and are reachable by keyboard.
  - Given a rating is submitted, when it succeeds, then the chosen control shows a selected state that survives a reload of the conversation.
  - Given a rating is submitted twice with different values, when the second succeeds, then the displayed state reflects the second and no duplicate is created.
  - Given the request fails, when the error returns, then the control reverts to unselected and an error toast is raised.

### EP-12: Administration

#### US-1201: Find and page through users

- **Story**: As an administrator, I want to search the user directory by name or email so that I can locate an account.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-203, US-104
- **Acceptance criteria**:
  - Given the users tab, when a term is typed, then `GET /api/users?name=` is sent debounced, and because the server matches first name, last name, **and** email with a `LIKE`, an email fragment finds the user — no separate email field is needed.
  - Given results, when they render, then the table shows avatar initials, full name, email, and the user's permission set as badges.
  - Given more results than one page, when the paginator is used, then `skip` and `take` drive the request and the current page is reflected in the URL query params.
  - Given no sort control, when the tab renders, then none is offered, because user search is server-paged and accepts no sort parameter — regime D.
  - Given a non-administrator reaching the route directly, when the request is issued, then the 403 `/problems/permission-required` renders naming the missing permission.

#### US-1202: Create a user

- **Story**: As an administrator, I want to provision a user ahead of their first sign-in so that they have the right permissions when they arrive.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-1201
- **Acceptance criteria**:
  - Given the create form, when it renders, then it collects an email address and a permission selection only — no Role, Status, Password, or Confirm Password fields, because the API has none and users are Entra-backed with a permission set.
  - Given a valid email, when submitted, then `POST /api/users` runs and the directory resolves the object id and display name.
  - Given an email not present in the directory, or a directory user with no email, when submitted, then the 404 renders against the email field explaining that the directory has no such user.
  - Given an email already belonging to an active user, when submitted, then the 400 validation message renders against the field and nothing is created.

#### US-1203: Edit a user's profile and permissions

- **Story**: As an administrator, I want to change a user's permissions and be told plainly that I am replacing the whole set so that I do not revoke a grant by omission.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-1201
- **Acceptance criteria**:
  - Given the edit form, when it opens, then every permission from `GET /api/permissions/all` renders as a checkbox with the user's current grants checked.
  - Given the form, when it renders, then copy states that saving replaces the user's entire permission set and that unchecking a permission revokes it, because `PUT /api/users/{id}` treats `permissionIds` as the complete desired set.
  - Given an administrator unchecking their own `Administrator` permission, when they save, then the server's 400 renders as a specific message explaining they cannot revoke their own administrator access.
  - Given MCP-derived permissions, when they render, then they are shown as managed by their owning server rather than as freely grantable.

#### US-1204: Deactivate a user

- **Story**: As an administrator, I want to deactivate an account so that a departed employee loses access.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-1201
- **Acceptance criteria**:
  - Given a user row, when deactivate is chosen, then a confirmation modal names the user and its destructive action is styled red.
  - Given confirmation, when `DELETE /api/users/{id}` succeeds, then the row is removed from the active list and a success toast is raised.
  - Given an administrator attempting to deactivate themselves, when they confirm, then the server's 400 renders as a specific message explaining they cannot deactivate their own account, and the row is restored.

#### US-1205: `[enabler]` Filter user search by permission (B10)

- **Story**: Add a `permissionId` filter to `GET /api/users`. The `name` parameter already covers first name, last name, and email, so email filtering needs nothing — only the permission filter is missing. Unblocks US-1206.
- **Priority**: P2 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given `GET /api/users?permissionId=`, when supplied, then only active users holding that grant are returned, paginated in the standard envelope.
  - Given both `name` and `permissionId`, when supplied together, then both filters apply.
  - Given a `permissionId` that does not exist, when supplied, then it returns 400 as a validation problem rather than an empty page.
  - Given the parameter is omitted, when the request is handled, then behavior is unchanged.

#### US-1206: Filter users by permission

- **Story**: As an administrator, I want to list everyone holding a given permission so that I can audit who has access.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-1205, US-1201
- **Acceptance criteria**:
  - Given US-1205 has not landed, when the permission filter is used, then it filters only the rows already loaded and the control is labelled to say so — it must not appear to filter the full directory.
  - Given US-1205 has landed, when the filter is used, then `permissionId` is sent, pagination reflects the filtered total, and the interim label is gone.
  - Given a filter selection, when it changes, then it is reflected in the URL query params.

#### US-1207: Manage the model catalog

- **Story**: As an administrator, I want to add, update, and retire models so that users see an accurate catalog.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-203
- **Acceptance criteria**:
  - Given the models tab, when it loads, then `GET /api/models/all` returns the complete list and client-side search over provider and model name is fully correct with no pagination — regime C.
  - Given the add or edit form, when it renders, then it binds `providerId`, `name`, `deploymentName`, `description`, `contextWindowSize`, `maxOutputTokens`, `isToolEnabled`, and `isDefault` — and does not show the mockup's invented "API Endpoint" or "Available To" fields.
  - Given a model is soft-deleted, when the change lands, then `adminEvents.modelCatalogChanged` fires and the root model catalog store refreshes so the chat model picker cannot offer a retired model.
  - Given a model marked `isDefault`, when saved, then the previous default is reflected correctly in the list after the response.
  - Given a validation failure, when it returns, then each `errors` key maps onto the matching form control.

#### US-1208: Manage MCP servers

- **Story**: As an administrator, I want to register and maintain MCP servers so that users can select the right tools.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-203
- **Acceptance criteria**:
  - Given the MCP tab, when it loads, then `GET /api/mcps/all` returns the administrative `McpServerDto` list including `url`, `authType`, `scope`, and `permissionId`, and client-side search is fully correct — regime C.
  - Given the add or edit form, when it renders, then it binds name, description, url, auth type, scope, and the linked permission.
  - Given a server is deactivated, when the change lands, then `adminEvents.mcpServerCreated` or its deactivation counterpart fires so the root MCP catalog and the chat picker refresh across the route-scope boundary.
  - Given a 502 or a validation failure on save, when it returns, then the message names the server and the form retains the entered values.

#### US-1209: Reach an admin tab by URL

- **Story**: As an administrator, I want to link a colleague straight to a specific admin tab so that we do not have to describe how to get there.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-203
- **Acceptance criteria**:
  - Given `/admin/users`, `/admin/models`, `/admin/mcps`, and `/admin/reports`, when each is opened directly, then that tab renders as the active one — tabs are routes, not local component state.
  - Given `/admin`, when opened, then it redirects to `/admin/users`.
  - Given a tab is opened, when its chunk loads, then only that tab's store is instantiated, scoped to the route.
  - Given the browser back button after switching tabs, when pressed, then the previous tab is restored.

### EP-13: Usage reports

#### US-1301: `[enabler]` Expose the usage audit trail over HTTP (B6)

- **Story**: Add a reporting API over the usage data. `ConversationUsage` and `ConversationUsageToolCall` already persist per-call token splits, durations, provider, model, tool names, and outcome — including abandoned turns and the out-of-band naming calls — but there is zero API surface over any of it. Unblocks US-1302.
- **Priority**: P2 · **Estimate**: L · **Depends on**: —
- **Acceptance criteria**:
  - Given `GET /api/reports/usage` with `from`, `to`, and `groupBy` across `model | provider | user | day`, when called by an administrator, then aggregated token counts and call counts are returned for the requested grouping.
  - Given a non-administrator, when the route is called, then it returns 403 `/problems/permission-required` naming the Administrator grant.
  - Given the outcome dimension, when results are returned, then completed, cancelled, and failed turns are distinguishable, because "tokens billed for answers nobody read" is the number the audit trail exists to surface.
  - Given a `from` later than `to`, or a range exceeding the configured maximum, when supplied, then it returns 400 as a validation problem naming the parameter.
  - Given the response, when it is produced, then it contains no prompt content, tool arguments, or tool results, consistent with the privacy posture of the data being reported on.

#### US-1302: See what the platform is spending

- **Story**: As an administrator, I want live usage reports so that I can answer what we spent and on what.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-1301, US-1209
- **Acceptance criteria**:
  - Given US-1301 has not landed, when the reports tab is opened, then `UnavailablePanelComponent` renders naming the missing API, and `ReportsStore` holds zero records — no sample chart, no placeholder totals.
  - Given US-1301 has landed, when the tab is opened, then a request is issued on **every** entry and no cached result is displayed, including on a back-navigation to the tab.
  - Given a date range and grouping control, when either changes, then a new request is issued and both are reflected in the URL query params.
  - Given a range returning no rows, when it renders, then an empty state names the range rather than showing zeroes as data.
  - Given the request fails, when the error renders, then the `traceId` is shown with a retry control.

### EP-14: Access and conformance gates

#### US-1401: Use the whole app from the keyboard

- **Story**: As a keyboard-only user, I want to complete every core task without a mouse so that the app is usable to me.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-606, US-903, US-1201
- **Acceptance criteria**:
  - Given the composer, when navigating by keyboard, then the textarea, attach, model picker, MCP picker, voice, and send/stop are all reachable in a logical tab order, and the pickers open, navigate, and select with arrow keys and Enter.
  - Given a modal or offcanvas, when it opens, then focus moves inside and is trapped, Escape closes it, and focus returns to the invoking control.
  - Given the full paths for send, stop, rename, and delete, when each is performed with the keyboard only, then it completes without a pointer.
  - Given any interactive element, when it receives focus, then a visible focus indicator meets WCAG 2.1 AA non-text contrast at 3:1.

#### US-1402: Follow a streaming answer with a screen reader

- **Story**: As a screen-reader user, I want to be told when the assistant is working and when new content arrives so that I am not left in silence.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-406, US-501
- **Acceptance criteria**:
  - Given a turn in flight, when it starts, then the transcript region carries `aria-busy="true"` and the streaming answer region is `aria-live="polite"`.
  - Given the toast stack, when a toast is raised, then it is announced through an `aria-live` region without stealing focus.
  - Given activity cards appearing during a turn, when they render, then announcements are not so frequent as to make the live region unusable — status changes are announced, individual text deltas are not.
  - Given the turn completes, when `Finished` arrives, then `aria-busy` is cleared.

#### US-1403: Use the app on a tablet or a phone

- **Story**: As a user away from my desk, I want the app usable at tablet and phone widths so that I can work from any device.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-301, US-1201
- **Acceptance criteria**:
  - Given a viewport at or above 1024px, when the app renders, then the sidebar is persistent, either expanded at 260px or a 60px strip.
  - Given a viewport between 768px and 1023px, when the app renders, then the sidebar defaults to the 60px strip and expands as an overlay with a backdrop.
  - Given a viewport below 768px, when the app renders, then the sidebar is hidden behind a navbar hamburger that opens it as a full-screen overlay, the composer is full-width with reduced padding, and the picker dropdowns are full-width.
  - Given a viewport below 768px, when an admin table renders, then it collapses to a card layout rather than overflowing horizontally.
  - Given each of the three breakpoints, when the chat, projects, documents, and admin routes are exercised, then no horizontal scrollbar appears on the document body.

#### US-1404: Respect a reduced-motion preference

- **Story**: As a user sensitive to motion, I want animations suppressed when my system says so.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-106
- **Acceptance criteria**:
  - Given `prefers-reduced-motion: reduce`, when a turn streams, then text appears without a typewriter animation and without a blinking cursor.
  - Given the same preference, when a toast is raised or dismissed, then it appears and disappears without the slide transition.
  - Given the same preference, when the sidebar collapses or a message appears, then the width and fade-slide transitions are suppressed.
  - Given the preference is not set, when the same interactions occur, then the specified 150ms, 200ms, and 300ms timings apply.

#### US-1405: `[enabler]` Gate accessibility and budgets in the build

- **Story**: Turn the accessibility and size criteria into automated gates so they cannot regress silently. Unblocks the success criteria in section 1.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-108, US-1401
- **Acceptance criteria**:
  - Given the chat, projects, documents, and admin routes, when the automated axe-core run executes in both light and dark themes, then it reports zero serious or critical violations and fails the build otherwise.
  - Given a build, when the initial chunk is measured, then it is within the configured budget and the build fails if the diagram or math library is present in it.
  - Given the test suite, when it runs with `provideCheckNoChangesConfig({ exhaustive: true })`, then no view depends on a mutated plain object — the two places the old client got this wrong were upload progress and attachment chips.
  - Given the vendored Andes contract, when CI runs, then the diff check from US-107 executes as part of the same pipeline.

## 8. Milestones & rollout

**Phases**, derived from the epic dependency graph.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **P1 — Foundation** | EP-1 in full (US-101 … US-108) | ~2 weeks |
| **P2 — Signed-in shell** | EP-2 in full, EP-3 P0 stories (US-301 … US-304, US-306) | ~1 week |
| **P3 — MVP chat** | EP-4 P0 stories (US-401 … US-407), EP-5 US-501, EP-6 US-601, US-602, US-606. **This is the minimum viable replacement** — the point at which the rebuilt client can do what the old one could, plus the timeline the old one never could | ~2.5 weeks |
| **P4 — Chat completeness** | EP-4 remainder (US-408 … US-410, US-412, US-413), EP-5 remainder, EP-6 remainder, EP-3 US-305 | ~1.5 weeks |
| **P5 — Library, files, projects** | EP-7, EP-8, EP-9 excluding US-909 and US-910, plus EP-3 US-307. US-307 sits in EP-3 by surface but depends on US-901, so it schedules here rather than with the rest of its epic. Backend starts US-907 (B1) in parallel with P3 so US-908 lands without its interim ceiling | ~2.5 weeks |
| **P6 — Administration** | EP-12 excluding US-1205 and US-1206 | ~1.5 weeks |
| **P7 — Conformance** | EP-14 in full. Gates run against everything shipped in P1–P6 | ~1 week |
| **P8 — Enabled features** | EP-10, EP-11, EP-13, plus US-706, US-902, US-909, US-910, US-1205, US-1206, US-411. Each pairs an enabler with the frontend story it releases; the enablers can start any time from P1 onward and the frontend halves land as they complete | ~2.5 weeks |

The backend enablers form an independent track: US-907 (B1), US-1001 (B2), US-909 (B3), US-1101 (B4), US-1102 (B5), US-1301 (B6), US-1004 (B7), US-706 (B8), US-411 (B9), and US-1205 (B10). Only US-1102 and US-1004 depend on another enabler; the rest can begin immediately. Scheduling any of them before P8 removes an interim behavior rather than adding one.

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| The old client is already broken by the framed SSE release, so there is no working frontend during the rebuild | Treat P3 as the deadline that matters. Nothing before it restores a usable client, and no scope should be added ahead of it |
| The reference store pattern set in `ConversationListStore` (US-302) is copied by 15 other stores; a wrong pattern is 16 rewrites | Review US-302 as a pattern decision before any second list store is written. The same applies to US-104, which every store composes |
| Streaming performance characteristics are fixed by US-405 and US-602; getting the coalescing or the head/tail split wrong is a rewrite of the chat surface | Both have unit-level criteria that run without a backend. Write `timeline.ts` and the codec against fixtures before the transport exists |
| `Andes.Extensions.AI.UI` is a package type, not ours — a major upgrade is an API change for every client | US-107's CI diff check fails the build on drift, so an upgrade surfaces as a red build rather than as a runtime mismatch |
| MSAL is a two-major jump and the old configuration cannot be ported | US-201 is written against the v6 API from its documentation; do not port the old `app.config.ts` factories from memory |
| A cancelled turn is billed but transcribed nowhere; appending the partial text to the transcript would make the visible history diverge from the model's context | US-407 makes the detached, explicitly-unsaved card an acceptance criterion rather than a styling choice |
| Interim behaviors for the ten gaps could quietly become permanent | Every interim behavior is an acceptance criterion on a story that also states the post-enabler behavior, so the interim state is visible in the backlog rather than buried in code |
| Client-side sort over a drained set silently misleads at the 500-item ceiling | US-902 and US-908 both require the ceiling notice as an acceptance criterion; US-705 forbids offering a sort the server cannot honor |

**Rollout & rollback.** There is no incremental rollout path and no rollback to the previous client: `enterprise-ui/` is deleted, and the old client cannot read the current stream regardless. The back-out for any individual capability is the feature flag in `config.json` — the diagram and math chunks are flagged, and the raw-text stream codec stays behind a flag as a fallback for a deployment still running the pre-framing server. Deployment is one artifact promoted across environments with `config.json` overwritten per environment, so a bad configuration is corrected by replacing one file rather than rebuilding. Each phase is independently deployable; P3 is the first phase worth deploying to users.

## 9. Assumptions & open questions

**Assumptions.** Each is a guess a reviewer can veto.

- The ten backend gaps are in scope for this document as `[enabler]` stories placed inside the feature epic each one unblocks, per the answer given during discovery. Backend engineers are a persona and the phase graph schedules enablers ahead of the stories they release.
- Concrete numeric targets not supplied in the request, proposed here for veto: the 500-item drain ceiling and `take=100` page size for regime A; the 200ms Stop responsiveness in US-407; the 2-second copy confirmation; the 4.5:1 and 3:1 contrast ratios (WCAG 2.1 AA). The initial-bundle budget number itself is `TBD` — US-108 requires that a budget exists and fails the build, but the byte figure must be set from the first real build of the scaffold rather than guessed now.
- Phase durations in section 8 are relative sizing derived from the story estimates, not a commitment, and assume no team size — none was specified and none is invented here.
- "Voice input" is assumed to mean browser-native speech recognition with graceful absence where unsupported (US-413), not a server-side transcription service. A server-side transcription requirement would make this a new backend capability and a new enabler.
- The Documents screen is assumed to show only the signed-in user's own documents, consistent with every other resource being owner-scoped and there being no administrative view of another user's data.
- Thumbs up/down is assumed to be a simple binary rating with no free-text comment. A comment field would change US-1102's contract.
- Project favorites (US-909) are assumed to be per-user, mirroring conversation favorites. If projects are ever shared, this becomes a different design.
- The interim device-local project pins (US-910) are assumed acceptable as a stopgap despite not syncing across devices; the alternative is to hide the sidebar project tree entirely until US-909 lands.
- Admin "Reports" is assumed to mean usage and cost reporting over the existing audit trail, since that is the only reporting data the platform persists. If it means something else — adoption, conversation volume, model quality — US-1301's contract is wrong.
- `docs/conversations/streaming-contract.md` is treated as the authority on wire behavior over the planning document, and is currently untracked in git. It is assumed it will be committed; if it is not, the acceptance criteria that cite it lose their reference.
- No telemetry story appears in this document because the client emits no analytics events today and none was requested. The success criteria in section 1 are all measured by tests and build gates rather than by runtime telemetry, which is why the absence does not leave them unmeasurable.

**Open questions.**

- **How should the Documents screen fetch across conversations?** US-1001's B2 contract lists one conversation's documents, which serves the grouped-by-conversation view through a fan-out but forces an N+1 over the conversation list for an unbounded library. A cross-conversation `GET /api/documents` scoped to the caller would serve the screen in one request. This is flagged as an acceptance criterion on US-1001 rather than decided here — *backend engineer, before US-1001 is estimated*.
- **What is the initial-bundle budget in bytes?** Needs a figure from the first real build of the scaffold — *frontend engineer, at the end of US-101*.
- **Should the client request the four cheap stream-protocol additions?** A leading `event: hello` frame carrying `{protocol, version}` for deterministic codec selection; a terminal failure frame, since "no `Finished`" is currently the only truncation signal; `: keep-alive` comments every 15s so intermediaries do not kill long tool calls; and `conversationName` on `Finished` to remove the post-first-turn refetch entirely. None is required by any story here, and `assistantMessageId` is already covered by US-1101 — *product owner and backend engineer*.
- **Is the 500-item ceiling the right number for regime A?** It trades the number of drain requests against how often a user hits a disabled sort. Should be revisited against real project and conversation counts — *product owner, after P5*.
- **Does adding columns for B3, B4, and B7 require the first EF Core migration?** `Repository/Migrations/` is empty and `Database.Migrate()` runs at startup; the repository's standards say to flag before adding the first migration because it changes startup behavior — *backend engineer, before US-909 or US-1101 starts*.
- **Should reports be exportable?** US-1302 specifies viewing only. CSV or Excel export is a plausible immediate follow-up and would extend US-1301's contract — *product owner*.
