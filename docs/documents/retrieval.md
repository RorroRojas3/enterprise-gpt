# Retrieval

The `document_search` tool: hybrid vector plus keyword search over ingested chunks, offered to the
model during a turn.

## Scope

`DocumentRetrievalService.GetScopeAsync` answers one question — what is this turn allowed to search?

A search reaches every active `ConversationDocument` of the conversation being answered, and, when
that conversation is in a project, every active `ProjectDocument` of the project. Documents come
back in upload order, each carrying a `DocumentSource` discriminator (`Conversation` or `Project`).
The discriminator is persisted nowhere; it exists so a conversation document and a project document
sharing a chunk index are never confused once both tables are unioned into one candidate set.

**The project is read from the conversation row, never taken from a caller.** This service cannot be
asked for one project's documents through another project's conversation. The project is read *and*
confirmed active in the same query, so a deactivated project yields a null `ProjectId`, switching
the project half of every retrieval statement off for the rest of the turn even though the
conversation still points at it.

**The scope is resolved once per turn**, before the stream starts, and the tool closes over it.
Resolving per call would re-run the authorization queries for every search and would let a document
soft-deleted mid-turn change the corpus underneath a half-answered question. The trade is that a
document uploaded *during* a turn is not searchable until the next one — the correct side of the
trade, since a turn's evidence base should not move while it is being answered.

### Soft delete is filtered by hand, at three levels

There is no `HasQueryFilter` in this model, so every query filters itself.

| Level | Predicate |
| --- | --- |
| Chunk | `ch.[DateDeactivated] IS NULL` |
| Document | `cd.[DateDeactivated] IS NULL` |
| Owner | conversation id matched directly; project dropped from scope when deactivated |

Each level has its own integration test, because a missing predicate at any one silently resurrects
deleted content into a grounded answer.

## Hybrid search

`SearchAsync` runs two independent passes over the same scoped candidate set and fuses their
**ranks**.

### Vector pass

The query is embedded through the same generator ingestion used, and the vector's length is checked
against the 1536 fixed by the `vector(1536)` column. A mismatch throws immediately with an
explanation rather than letting SQL Server reject it with a type error that says nothing about the
cause — a wrong-width vector means the configured deployment is not the one documents were ingested
with.

```sql
SELECT TOP (@candidates)
       c.[DocumentId], c.[Source], c.[Index],
       VECTOR_DISTANCE('cosine', @queryVector, c.[Embedding]) AS [Distance]
FROM ( /* conversation chunks UNION ALL project chunks, soft-delete filtered */ ) AS c
ORDER BY [Distance];
```

`VECTOR_DISTANCE('cosine', ...)` returns a **distance in [0, 2]** — smaller is closer — not a
similarity. Row order is the pass rank. The search is exact kNN, not approximate.

### Keyword pass

Embeddings are weak at exact tokens, which is a large share of what people search enterprise
documents for.

`ExtractTerms` lower-cases letter-or-digit runs, drops about a hundred common English stop words,
and keeps a token that is at least three characters, or two characters *containing a digit* — which
keeps `v2` and `5g` while dropping the noise a two-letter word adds. Survivors are ordered
tokens-with-digits first, then longest first, as a cheap stand-in for inverse document frequency.
The first `MaxQueryTerms` (default 8) are searched.

Each term becomes one parameterized `LIKE` arm, summed into a match count:

```sql
CASE WHEN c.[Text] COLLATE Latin1_General_CI_AI LIKE @t0 ESCAPE '\' THEN 1 ELSE 0 END
+ CASE WHEN c.[Text] COLLATE Latin1_General_CI_AI LIKE @t1 ESCAPE '\' THEN 1 ELSE 0 END
```

Three deliberate choices:

- **`LIKE`, not `CONTAINS` / `FREETEXT`.** Full-Text Search is a separately installed component that
  cannot be assumed present. Over a row set already narrowed to one conversation, a substring scan
  is the same cost class as the vector scan beside it.
- **An explicit `Latin1_General_CI_AI` collation**, so matching is case- and accent-insensitive
  whatever collation the database was created with. It costs nothing, because there is no index on
  `Text` to lose.
- **Only the number of arms varies with the query.** Placeholders come from a counter and every term
  value is bound as a parameter, escaped for `LIKE` wildcards by the caller. No term value is ever
  concatenated into command text.

Ties break towards the shorter chunk: when two chunks contain the same terms, the denser one is
better evidence. Setting `Documents:Retrieval:EnableLexicalSearch` to `false` falls back to
vector-only.

### Reciprocal rank fusion

A cosine distance and a term count cannot be added, so the passes are fused on rank alone:

```text
score(chunk) = sum over passes  1 / (k + rank)      k = 60, rank is 1-based
```

`k = 60` damps the influence of the very top ranks, so a chunk **both** passes agree on beats one
that either pass ranks first alone. Fusing on `(DocumentId, Source, Index)` keeps the cosine
distance from whichever pass supplied one, and the larger match count.

### The relevance gate

Nearest-neighbour search always returns *something*. Without a gate, a question the documents do not
answer comes back with the least-bad chunks in the corpus — which reads to the model exactly like
grounded evidence. A fused candidate survives only if:

- it has a distance and that distance is at most `MaxDistance` (default **0.62**), **or**
- it matched at least `min(2, max(termCount, 1))` query terms.

The second arm makes a keyword-only hit earn its place: one matching term out of several is usually
the query's most common word appearing somewhere irrelevant. When only one term was searched, one
match is enough.

### Caps

Survivors are ordered by fused score and taken greedily under two caps: `MaxPassagesPerDocument`
(default 3), so one loud document cannot crowd out every other, and `MaxResults` (default 8).
Over-fetching — `CandidateCount` 40 against `MaxResults` 8 — is what gives fusion and the
per-document cap something to choose between; startup validation rejects a candidate count below the
result count for exactly that reason.

## Configuration — `Documents:Retrieval`

| Key | Default | Effect |
| --- | --- | --- |
| `MaxResults` | 8 | Passages returned to the model |
| `MaxPassagesPerDocument` | 3 | Per-document cap |
| `CandidateCount` | 40 | Rows fetched per pass before fusion |
| `MaxDistance` | 0.62 | Vector relevance gate |
| `MaxQueryTerms` | 8 | Keyword arms |
| `EnableLexicalSearch` | true | Turns the keyword pass off |

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs` | Scope, passes, fusion, gate |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentRetrievalSql.cs` | The two statements |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentRetrievalModels.cs` | Scope and result shapes |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentTool.cs` | The model-facing tool declaration |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/Persistence/` | Soft-delete levels and ranking |

## Related

- [ingestion.md](ingestion.md)
- [sheet-query.md](sheet-query.md)
- [summarization.md](summarization.md)
