# Downloads and Generated Files

Reading a file back out — one the user uploaded, or one the assistant produced.

## The download is a signed link, not a proxy

A download is one request, and **the file never passes through the API**:

1. `GET api/documents/conversations/{conversationId}/{documentId}` (or the project equivalent)
   authorizes the caller against the **parent** conversation or project.
2. The service signs a short-lived Azure Storage service SAS over the blob ingestion wrote.
3. The response is `200` with `Cache-Control: no-store` and a small JSON body: the signed URL, the
   original file name, and when the URL stops working.
4. The client hands that URL to the browser as a detached `<a download>` click — not a navigation
   and not a fetch — and Azure Storage serves the bytes.

**Why not proxy.** `Documents:MaxFileSizeBytes` defaults to 50 MB, and unlike the upload path
nothing bounds read concurrency: ingestion caps queued work and in-flight work, whereas a download
is a plain `GET` any number of callers can issue at once. Proxying would put all of that traffic —
and, if buffered anywhere, all of that memory — on the API process, to add nothing storage does not
already do.

**What it costs.** The link carries its own authorization, so for its lifetime it is a **bearer
credential**. Treat a leaked link as a leaked file. Every other decision here follows from that.

| Route | Access | Failures |
| --- | --- | --- |
| `GET api/documents/conversations/{conversationId:guid}/{documentId:guid}` | The conversation owner | 401, 404, 503 |
| `GET api/documents/projects/{projectId:guid}/{documentId:guid}` | The project owner | 401, 404, 503 |

There is **no 403**, because there is no permission filter — an unowned document is a 404 that reads
identically to every other miss. There is no 400, because both route parameters are constrained to
`:guid` and a malformed one simply does not match the route.

Ingestion writes no `BlobHttpHeaders`, only metadata, so the stored object carries neither the
original file name nor a content type. The SAS therefore has to sign both in as response-header
overrides (`rscd` and `rsct`) — that is what makes the browser save `q3-budget.xlsx` rather than a
GUID.

A storage account that cannot sign (no shared key available) raises `StorageNotConfiguredException`,
mapping to 503 `storage-not-configured`.

## Generated files

Files the assistant produces are the same entity with a different discriminator.

```csharp
public enum ConversationDocumentTypes
{
    Uploaded = 1,
    Generated = 2
}
```

Numbered from 1 so an unset column is distinguishable from a legitimate value. It lives on
`ConversationDocument.Type` and **not** on `BaseDocument` — `ProjectDocument` is untouched, so a
project-scoped generated file stays out of scope entirely.

The column is configured `HasConversion<int>()` with a store `HasDefaultValue(Uploaded)` and
`ValueGeneratedNever()`. EF scaffolds a migration's `AddColumn` from the CLR default alone, so
without an explicit store default every existing row would backfill to the enum's zero value, which
this enum deliberately does not define. The migration adds the column with `defaultValue: 1`, so
every pre-existing row reads back as `Uploaded`.

`DocumentRetrievalSql`'s hand-written SQL depends on there being no `HasColumnName` override on this
configuration.

Over the wire the discriminator is serialized as the enum **member name** — `"Uploaded"` /
`"Generated"` — through a per-property `JsonStringEnumConverter` rather than a global one, so every
other enum this API sends is still a number. `GET api/documents` accepts
`?type=uploaded|generated`. The per-conversation listing keeps its `Uploaded`-only filter and gained
no parameter: a generated file reaches its reader through the chip on the message that produced it.

### A second container

A generated file never lands in the container an upload uses.
`AzureStorage:GeneratedContainer` names a second one, so an uploaded file and a generated one never
share a retention policy, an access policy or a lifecycle rule. Both settings are read on **each
use**, not cached at construction, so a reloaded configuration takes effect without a restart.

`DocumentStorage` is the internal static helper both write paths share, so the two cannot drift on
either fact: `RequireContainer(...)` reads and validates a container name, and
`ResolveContentType(extension)` is the one extension-to-content-type map, so a `.xlsx` served from
either container gets an identical `Content-Type`.

Blob keys keep the existing `{userId}/{conversationId}/{documentId}{extension}` shape. They cannot
collide with an uploaded blob's key despite being identically shaped, because the two live in
different containers.

### Persisted, not ingested

A generated file is written and recorded, but **not** chunked or embedded, so it never enters the
retrieval corpus. Retrieval isolation therefore rests on two independent guarantees: no chunks
exist, and the retrieval SQL filters on the discriminator anyway.

A file the turn did not deliver is **withdrawn** rather than left behind, so an interrupted turn does
not leave an orphan the user can see but never reach.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/DocumentService.cs` | Download authorization and SAS signing |
| `enterprise-gpt-api/Enterprise.Gpt.Service/BlobStorageService.cs` | `GenerateSasUri`, and the 503 when it cannot |
| `enterprise-gpt-api/Enterprise.Gpt.Service/DocumentStorage.cs` | Container resolution and the content-type map |
| `enterprise-gpt-api/Enterprise.Gpt.Service/GeneratedDocumentService.cs` | Persisting and withdrawing generated files |
| `enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ConversationDocumentTypes.cs` | The discriminator |
| `enterprise-gpt-ui/src/app/core/documents/document-download-store.ts` | The detached anchor click |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/` | Ownership and 404-uniformity tests |

## Related

- [ingestion.md](ingestion.md)
- [../tools/file-agent.md](../tools/file-agent.md)
- [../architecture/auth-and-permissions.md](../architecture/auth-and-permissions.md)
