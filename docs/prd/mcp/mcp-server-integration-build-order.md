# Build order: MCP server integration hardening

A dependency-resolved execution sequence for the 20 stories in [`mcp-server-integration.md`](mcp-server-integration.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives four waves from each story's `Depends on` field: a wave is a set of stories whose dependencies are all satisfied by an earlier wave, so every story in a wave can be picked up concurrently by however many people are available. Where this file and the PRD disagree, the PRD wins and this file is wrong.

Status here mirrors the `Status` field each story would carry in the PRD once work starts. Update both, or update the PRD and re-derive this.

**Progress: 0 / 20 done.**

**Critical path.** Two chains tie for longest at four waves deep, and both share the same root: `US-101` (`[enabler]` multi-scope schema) → `US-103` (dry-run validation) → `US-105` (enforce at save) → `US-106` (override) is EP-1's registration-enforcement chain; `US-101` → `US-201` (`[enabler]` scopes/claims on the 403) → `US-202` (carry the claims challenge) → `US-205` (pre-flight before the SSE stream) is EP-2's consent chain. Scheduling `US-101` in wave 1 — even though nothing in EP-1's own value proposition needs it before `US-103` — is what lets the consent chain start as early as its first real dependency allows; deferring `US-101` behind unrelated EP-1 work would push EP-2 out by a full wave for no reason. **Maximum useful concurrency is 8**, in wave 2, where every enabler two epics deep has already landed and nothing yet needs a second wave-2 output.

## Wave 1 — independent enablers and stories

Nothing in this wave depends on anything else in the PRD; all five can start on day one, in parallel.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | US-101 `[enabler]` Support multiple OAuth scopes per MCP server | — | P0 | US-102, US-104, US-301, US-401 | |
| 1 | 2 | US-102 Enforce HTTPS on MCP server URLs outside Development | — | P1 | US-101, US-104, US-301, US-401 | |
| 1 | 3 | US-104 View a registered server's tool surface | — | P1 | US-101, US-102, US-301, US-401 | |
| 1 | 4 | US-301 `[enabler]` Move the OBO token cache to SQL Server | — | P0 | US-101, US-102, US-104, US-401 | |
| 1 | 5 | US-401 `[enabler]` Register an MCP ActivitySource and Meter | — | P0 | US-101, US-102, US-104, US-301 | |

## Wave 2 — first-order dependents

Every story here depends on exactly one wave-1 story and nothing else, so all eight are unblocked the moment their single prerequisite lands — they do not have to wait for the whole of wave 1.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 6 | US-103 Dry-run validate a candidate MCP server registration | US-101 | P0 | US-201, US-203, US-302, US-303, US-304, US-402, US-404 | |
| 2 | 7 | US-201 `[enabler]` Carry consent scopes and the claims challenge on the MCP authorization problem | US-101 | P0 | US-103, US-203, US-302, US-303, US-304, US-402, US-404 | |
| 2 | 8 | US-203 Expose auth type and scopes on the user-facing MCP listing | US-101 | P1 | US-103, US-201, US-302, US-303, US-304, US-402, US-404 | |
| 2 | 9 | US-302 `[enabler]` Encrypt the token cache with a durable Data Protection key ring | US-301 | P0 | US-103, US-201, US-203, US-303, US-304, US-402, US-404 | |
| 2 | 10 | US-303 Refresh the MCP bearer token per request instead of at client creation | US-301 | P0 | US-103, US-201, US-203, US-302, US-304, US-402, US-404 | |
| 2 | 11 | US-304 Feature-flag the distributed token cache with a rollback to in-memory | US-301 | P1 | US-103, US-201, US-203, US-302, US-303, US-402, US-404 | |
| 2 | 12 | US-402 Emit MCP connect, tool-invocation, and token-acquisition metrics | US-401 | P0 | US-103, US-201, US-203, US-302, US-303, US-304, US-404 | |
| 2 | 13 | US-404 Structured MCP logging with no sensitive content | US-401 | P1 | US-103, US-201, US-203, US-302, US-303, US-304, US-402 | |

## Wave 3 — second-order dependents

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 14 | US-105 Reject a broken registration at save time | US-103 | P0 | US-202, US-204, US-403, US-405 | |
| 3 | 15 | US-202 Carry the Conditional Access claims challenge through token acquisition | US-201 | P0 | US-105, US-204, US-403, US-405 | |
| 3 | 16 | US-204 Report per-server consent state for the caller | US-203 | P1 | US-105, US-202, US-403, US-405 | |
| 3 | 17 | US-403 Emit registration-validation metrics | US-401, US-103 | P1 | US-105, US-202, US-204, US-405 | |
| 3 | 18 | US-405 Admin-visible last-known health per server | US-402 | P2 | US-105, US-202, US-204, US-403 | |

## Wave 4 — final layer

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 19 | US-106 Override registration validation for a server unreachable from the API host | US-105 | P1 | US-205 | |
| 4 | 20 | US-205 Pre-flight consent before the SSE stream opens | US-202 | P0 | US-106 | |
