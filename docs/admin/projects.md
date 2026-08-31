# Projects

A project groups conversations, holds standing instructions applied to every turn inside it, and
owns a document set those conversations can search.

## API surface

| Verb | Route | Notes |
| --- | --- | --- |
| GET | `api/projects` | The caller's projects |
| GET | `api/projects/{id}` | 404 for unknown, deactivated, or another user's |
| POST / PUT | `api/projects`, `api/projects/{id}` | Create and update |
| DELETE | `api/projects/{id}` | Soft delete with a cascade |
| PUT | `api/projects/{id}/favorite` | 204, no body |
| GET | `api/projects/{id}/documents` | The project's documents |
| DELETE | `api/projects/{projectId}/documents/{documentId}` | Soft-deletes chunks then document |

Ownership failures are **404, never 403**, so the routes cannot be used to probe for project ids.

## Membership is set from the conversation side

`ProjectId` is set through the conversation API. A project never adopts conversations; conversations
join it.

| Route | Behaviour |
| --- | --- |
| `POST api/conversations` with `projectId` | Creates the conversation inside the project |
| `PUT api/conversations` with `projectId` | **Replaces** the value |
| `GET api/conversations/search?projectId=` | Lists one project's conversations |

Both action DTOs validate `ProjectId` with `NotEmpty().When(x => x.ProjectId.HasValue)`: only
"omitted" and "a real id" are meaningful, and an all-zeros guid is a client bug that would otherwise
reach the ownership check and return a confusing 404.

Ownership is verified on create, on update **and on the listing filter**. On create the check runs
*before* the transaction opens, so a bad `projectId` creates nothing at all.

### `PUT api/conversations` is a full-representation replace

This is the most surprising part of the contract. The update writes all three fields:

```csharp
.ExecuteUpdateAsync(s => s
    .SetProperty(x => x.Name, request.Name)
    .SetProperty(x => x.ProjectId, request.ProjectId)
    .SetProperty(x => x.DateModified, date), ...)
```

**An omitted `projectId` removes the conversation from its project.** A client that only means to
rename must echo the current project id back, or the rename silently orphans the conversation. There
is no `PATCH`.

Removal is the only way this is expressed, which is why it is not an accident: sending
`"projectId": null` is how a client takes a conversation out of a project, and treating `null` as
"leave unchanged" would leave no way to express removal without a second route.

The client answers this with one `toUpdateBody()` helper every update path builds its body through,
so no caller can omit the field by accident. The move flow is the one path that legitimately sends
`null`, and therefore the one that must never let an `undefined` reach the body.

### Why there is no `GET api/projects/{id}/conversations`

One filter on the existing search route means one paging contract, one ordering rule and one place
the ownership check lives, rather than a second listing to keep in step with the first.

The existence check matters most for a **deactivated** project: the delete cascade sets
`ProjectId = NULL` on every conversation it held, so a filter without that check would answer a
cheerful empty page for a project that had rows before it was deleted.

## Instructions at chat time

When a conversation belongs to a project, the turn reads that project's `Instructions` and carries
them on **`ChatOptions.Instructions`** — not as a message in the transcript.

That placement has two consequences worth knowing: the instructions are not transcribed, so they do
not appear in an export and are not replayed as history; and they are counted separately by
`PromptOverheadCalculator`, because they ride outside the message list the context budget sums.

## Documents

Project documents ingest through the same pipeline as conversation documents into the parallel
`ProjectDocument*` tables, and are reachable by `document_search` from any conversation in the
project. See [../documents/ingestion.md](../documents/ingestion.md) and
[../documents/retrieval.md](../documents/retrieval.md).

## Delete semantics

`DELETE api/projects/{id}` soft-deletes four things and hard-deletes nothing:

| Rows | What happens |
| --- | --- |
| `ProjectDocumentChunk` for the project's documents | `DateDeactivated` set |
| `ProjectDocument` for the project | `DateDeactivated` set |
| `Conversation` with this `ProjectId` | `ProjectId` set to `NULL` — **the conversations survive** |
| `Project` | `DateDeactivated` set |

All four run as `ExecuteUpdateAsync` statements inside **one explicit transaction**. A partial
failure would otherwise leave conversations pointing at a deleted project, or chunks alive under a
deleted document.

**Conversations are released, not deleted.** They keep their own documents and transcripts and
become standalone, losing access to the project's documents and instructions. Deleting a project is
a filing decision, not a data-destruction one, and a user would not expect months of chat history to
go with it. Already-deactivated conversations are cleared too — the filter has no `DateDeactivated`
condition — so a future restore cannot resurrect a link to a deleted project.

**Blobs are never removed.** The row is only soft-deleted; reclaiming storage belongs to a retention
job that can also handle blobs orphaned by failed ingestions. A blob whose *ingestion* failed is
deleted immediately by the pipeline's own cleanup.

Deleting an already-deleted project is a 404, not a silent success.

## The client

`ProjectLookupStore` is root-scoped and holds the whole owned-project set, feeding the picker, the
sidebar favourites and the composer chip. The detail route provides three stores together —
`ProjectStore`, `ProjectDocumentsStore`, `ProjectConversationsStore` — and its instructions tab
carries a `canDeactivate` guard for unsaved edits.

**A file attached through the composer is not a project document.** The detail screen's composer
exists to start a conversation, so a file dropped there travels with that conversation and becomes
one of *its* documents once the send creates it — not one of the project's. Only the Files tab
writes to the project's own document set. The two surfaces run on two separate `UploadStore`
instances for exactly this reason (see [../frontend/state.md](../frontend/state.md#the-stores)):
the project-bound one backs the Files tab, and the composer gets its own, unbound one, so nothing it
attaches is uploaded until a conversation exists to own it. The files then reach that conversation
through `PendingAttachmentsStore`, alongside the prompt itself, because the screen that created the
conversation navigates away before ingestion can start.

`ProjectConversationsStore` lives in `core/` rather than the feature, so `features/shell` can reach
it without violating the layer direction.

The project route uses `loadChildren` rather than `loadComponent`, so the grid and the detail screen
share one layout instance where the project dialogs mount.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/ProjectService.cs` | CRUD and the transactional cascade |
| `enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs` | `EnsureProjectExistsAsync`, instruction injection |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ProjectEndpoints.cs` | The routes |
| `enterprise-gpt-ui/src/app/core/projects/project-lookup-store.ts` | The root project set |
| `enterprise-gpt-ui/src/app/features/projects/detail/` | The detail route and its three stores |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/` | Cascade and ownership tests |

## Related

- [../documents/ingestion.md](../documents/ingestion.md)
- [../conversations/turn-lifecycle.md](../conversations/turn-lifecycle.md)
- [users-and-permissions.md](users-and-permissions.md)
