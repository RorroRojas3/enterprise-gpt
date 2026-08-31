# Application Insights KQL Cookbook

Runnable [KQL](https://learn.microsoft.com/azure/data-explorer/kusto/query/) queries against this API's telemetry shape: the access log's structured fields, the chat pipeline's spans and metrics, and the two health routes. [telemetry.md](telemetry.md) covers why each signal exists and where it is registered.

Every query here has been checked against the field names in `RequestLogMessages.cs`, `ChatMetrics.cs` and the OpenTelemetry Semantic Conventions for Generative AI that `Microsoft.Extensions.AI`'s `OpenTelemetryChatClient` implements — not against a generic template. If a query returns nothing, check first that `AzureMonitor:ConnectionString` is configured — no connection string, no data.

## How to use this cookbook

**Schema at a glance** — which table holds which signal :

| Table | Holds |
|---|---|
| `traces` | The access log (events 1000–1004, via `ILogger`) — this app's own structured fields |
| `requests` | One row per inbound HTTP request, auto-collected by the ASP.NET Core instrumentation |
| `dependencies` | Outgoing calls: SQL, HTTP, and every LLM turn (`Enterprise.Gpt.Chat`) |
| `exceptions` | Exceptions the instrumentation recorded against a span — see the caveat in [Tool-call and pipeline-run failures](#tool-call-and-pipeline-run-failures) |
| `customMetrics` | Every OpenTelemetry metric: `gen_ai.client.token.usage` and the ten `enterprise_gpt.*` instruments in `ChatMetrics` |

Two habits that every recipe below depends on:

- **`customDimensions` is a dynamic column, and most of its interesting keys contain dots** — `gen_ai.request.model`, `sheet_query.outcome`. Kusto's dot-navigation syntax (`customDimensions.Foo`) parses a literal dot as a *nested* property access, so a dotted key silently returns `null` unless you index it as a string: `customDimensions["gen_ai.request.model"]`. Bracket notation is used throughout this document for that reason, even on the plain PascalCase keys the access log writes, so every recipe here is copy-paste safe.
- **The access log's four event kinds share one table with no queryable event name.** `RequestLoggingMiddleware` writes events 1000–1004 to `traces`, and the exporter does not carry the `.NET` `EventId`/`EventName` through as its own column. Discriminate by the fields each event actually sets instead — `customDimensions["StatusCode"]` exists **only** on a completion record (1001), which is the discriminator every query below that reads `traces` relies on.

## Correlating one request end to end by `traceId`

Every problem response this API returns carries a `traceId` — the 32-hex W3C trace id, from `ProblemDetailsRegistration`. A user or a support ticket quoting it finds the whole request, across every table, by `operation_Id`:

```kusto
union traces, requests, dependencies, exceptions
| where operation_Id == "a1b2c3d4e5f60718293a4b5c6d7e8f90"
| order by timestamp asc
| project timestamp, itemType, name, message, customDimensions
```

One thing to know about what this can and cannot guarantee: **the `traces` row is always there; the `requests`/`dependencies` rows might not be.** `AzureMonitor:EnableTraceBasedLogsSampler` ships `false` (see [telemetry.md](telemetry.md)) specifically so the access log stays a census independent of span sampling. OpenTelemetry samples at the **trace** level — the root span's decision is propagated to every child — so a trace the sampler dropped is missing its `requests` row and every `dependencies` child *together*, never partially. If the query above returns a `traces` row and nothing else, that is what happened: the request completed normally and was simply not one of the ~5-per-second the exporter kept as a span.

## Error rate and p95 latency by route

**Fast and native**, from the auto-collected `requests` table — subject to trace sampling, so treat the error rate as directionally accurate and re-run [the sampling-rate check](#a-note-on-sampling-and-itemcount) before trusting the p95 on a quiet route:

```kusto
requests
| where timestamp > ago(1h)
| summarize
    Total = sum(itemCount),
    Failed = sumif(itemCount, success == false),
    P95Milliseconds = percentile(duration, 95)
  by name
| extend ErrorRatePercent = round(100.0 * Failed / Total, 2)
| order by ErrorRatePercent desc
```

**Exact, from the access log** — every row is a census (traces are never sampled here), and it distinguishes an error from a visitor closing a browser tab, which `requests.success` does not: `Outcome` is `Faulted` only when an exception escaped the pipeline, never for `ClientAborted` (see [telemetry.md](telemetry.md) for why the two are kept apart):

```kusto
traces
| where timestamp > ago(1h)
| where isnotempty(customDimensions["StatusCode"])   // completion records only — see "How to use this cookbook"
| extend
    RouteTemplate = tostring(customDimensions["RouteTemplate"]),
    Outcome = tostring(customDimensions["Outcome"]),
    ElapsedMs = todouble(customDimensions["ElapsedMilliseconds"])
| summarize
    Total = sum(itemCount),
    Faulted = sumif(itemCount, Outcome == "Faulted"),
    P95Milliseconds = percentile(ElapsedMs, 95)
  by RouteTemplate
| extend ErrorRatePercent = round(100.0 * Faulted / Total, 2)
| order by ErrorRatePercent desc
```

`RouteTemplate` is `null` for an unrouted request (a 404 routing never matched) — group those separately if you need to see them, since `summarize` puts every `null` in one bucket.

## LLM spend by model and by user

`Microsoft.Extensions.AI`'s `OpenTelemetryChatClient` — wired through `UseEnterpriseTelemetry` — sets `gen_ai.usage.input_tokens`/`gen_ai.usage.output_tokens` directly on the LLM call's `dependencies` span, alongside `gen_ai.request.model` and `gen_ai.provider.name`:

```kusto
dependencies
| where timestamp > ago(1d)
| where isnotempty(customDimensions["gen_ai.request.model"])
| extend
    Provider = tostring(customDimensions["gen_ai.provider.name"]),
    Model = tostring(customDimensions["gen_ai.request.model"]),
    InputTokens = toint(customDimensions["gen_ai.usage.input_tokens"]),
    OutputTokens = toint(customDimensions["gen_ai.usage.output_tokens"])
| summarize InputTokens = sum(InputTokens * itemCount), OutputTokens = sum(OutputTokens * itemCount)
  by Provider, Model
| order by InputTokens desc
```

**By user** needs a join, not another attribute: `EndUserEnrichingProcessor` only tags `ActivityKind.Server` spans with `enduser.id` — an LLM call is a `Client` span, deliberately left untagged, because attributing a dependency to the *caller's* identity would misattribute work a server span already owns. The chat span's `user_AuthenticatedId` isn't on it; the request that made the call is, and they share `operation_Id`:

```kusto
dependencies
| where timestamp > ago(1d)
| where isnotempty(customDimensions["gen_ai.request.model"])
| extend
    Model = tostring(customDimensions["gen_ai.request.model"]),
    InputTokens = toint(customDimensions["gen_ai.usage.input_tokens"]),
    OutputTokens = toint(customDimensions["gen_ai.usage.output_tokens"])
| join kind=inner (requests | project operation_Id, UserId = user_AuthenticatedId) on operation_Id
| summarize InputTokens = sum(InputTokens * itemCount), OutputTokens = sum(OutputTokens * itemCount)
  by UserId, Model
| order by InputTokens desc
```

**Read `InputTokens * itemCount` as an estimate, not a ledger.** Under sampling, a kept span stands in for `itemCount` equivalent spans the sampler dropped — correct for *counting occurrences* (§ below), but this multiplies the one kept span's own token count across all of them, which assumes the dropped siblings used the same number of tokens. They usually didn't. For an exact, unsampled total — at the cost of losing the per-user breakdown — use the metric in the next recipe instead: custom metrics are **never** sampled in Application Insights, which is exactly the gap `gen_ai.client.token.usage` exists to close.

## Token usage over time

`gen_ai.client.token.usage` is a histogram, recorded once per LLM call, tagged `gen_ai.token.type` (`input`/`output`). Unlike the spans above, this is exact regardless of trace sampling:

```kusto
customMetrics
| where timestamp > ago(7d)
| where name == "gen_ai.client.token.usage"
| extend
    TokenType = tostring(customDimensions["gen_ai.token.type"]),
    Model = tostring(customDimensions["gen_ai.request.model"])
| summarize Tokens = sum(valueSum), Calls = sum(valueCount) by bin(timestamp, 1h), TokenType, Model
| order by timestamp asc
```

`valueSum` is the aggregation period's total (what you want for spend); `valueCount` is how many chat calls contributed to it, so `Tokens / Calls` in a follow-up `extend` gives the average prompt or completion size per call without a second query.

## Tool-call and pipeline-run failures

Every tool-shaped pipeline in this API — sheet queries, spreadsheet ingestion, document summarization, the File Agent — records its own outcome-tagged duration histogram in `ChatMetrics`, each under its own dimension name. `case()` normalizes the four into one column:

```kusto
customMetrics
| where timestamp > ago(1d)
| where name in (
    "enterprise_gpt.sheet_query.duration",
    "enterprise_gpt.sheet_ingestion.duration",
    "enterprise_gpt.document_summary.run.duration",
    "enterprise_gpt.file_agent.run.duration")
| extend Outcome = case(
    name == "enterprise_gpt.sheet_query.duration", tostring(customDimensions["sheet_query.outcome"]),
    name == "enterprise_gpt.sheet_ingestion.duration", tostring(customDimensions["sheet.outcome"]),
    name == "enterprise_gpt.document_summary.run.duration", tostring(customDimensions["summary.outcome"]),
    tostring(customDimensions["file_agent.outcome"]))
| where Outcome != "success"
| summarize Failures = sum(valueCount) by Tool = name, Outcome
| order by Failures desc
```

`valueCount`, not `valueSum`: for a duration histogram, the count of observations *is* the count of runs — one run, one recorded duration. A generated artifact that opened but did not match its claimed shape is a different signal, recorded as its own counter rather than folded into the run outcome above:

```kusto
customMetrics
| where timestamp > ago(7d)
| where name == "enterprise_gpt.file_agent.verification"
| where tostring(customDimensions["file_agent.outcome"]) == "failed"
| summarize FailedArtifacts = sum(value) by DocumentType = tostring(customDimensions["document.type"]), bin(timestamp, 1d)
```

Note `sum(value)` here, not `valueSum`: `FileAgentVerification` is a `Counter<long>`, and for a counter the preaggregated total lands in `value` (`valueSum` is meaningful for histograms). See [telemetry.md](telemetry.md) for the instrument-kind table.

**A caveat on `exceptions`.** Every fault this API can raise is caught by one of three `IExceptionHandler`s and turned into a Problem Details response before it ever reaches ASP.NET Core's own unhandled-exception path — which is what usually populates the `exceptions` table. Look for a genuine application fault in `traces` first (`Outcome == "Faulted"` on the completion record, from the [error-rate recipe](#error-rate-and-p95-latency-by-route) above, which also carries the exception's message) or in `requests` (`resultCode >= 500`). Treat a hit in `exceptions` as noteworthy precisely because it means something escaped the handler chain itself.

## Document-ingestion outcomes (spreadsheets)

**Scope note first:** only spreadsheet extraction (`.xlsx`/`.csv`) has a dedicated outcome metric. The general document pipeline's status — PDF, Office documents, everything `Azure Document Intelligence` extracts — lives in the in-memory `JobStatus` and the SQL-backed `Core.{Conversation,Project}DocumentPage`/`Chunk` tables, not in Application Insights; see [the ingestion status model](../documents/ingestion.md). What follows is real telemetry for the subset that has it:

```kusto
customMetrics
| where timestamp > ago(1d)
| where name == "enterprise_gpt.sheet_ingestion.duration"
| extend
    DocumentType = tostring(customDimensions["document.type"]),
    Outcome = tostring(customDimensions["sheet.outcome"])
| summarize Uploads = sum(valueCount) by DocumentType, Outcome
| order by Outcome, DocumentType
```

A `refused` outcome is a `Sheets:*` ceiling doing its job, not a bug — see [the spreadsheet ceilings](../documents/ingestion.md) for what each means and which one a spike in `refused` points at.

## Readiness and liveness

`GET /health` (liveness) checks nothing and returns the plain text `Healthy` with no body structure — it exists so a database outage cannot also read as "process is down" and trigger a restart. `GET`/`HEAD /health/ready` (readiness) runs the SQL and Cosmos health checks, cached for three seconds behind `ReadinessProbe`'s single-flight gate, and answers `{"status": "Healthy" | "Degraded" | "Unhealthy"}` with a matching `200`/`503`. Both are anonymous and mapped in every environment, and — because the route is anonymous — the body never names *which* dependency is down; that goes to the log instead.

`requests` gets a row for both routes on every hit, regardless of the access log's own `ExcludedPaths` (the access log's `ExcludedPaths` only suppresses this app's *own* log on success — the framework's request instrumentation is unconditional):

```kusto
requests
| where timestamp > ago(15m)
| where name has "/health"
| summarize Hits = sum(itemCount), Unhealthy = sumif(itemCount, resultCode == "503") by bin(timestamp, 1m), name
| order by timestamp desc
```

An empty result for longer than your platform's own probe interval means the probe stopped reaching the app, not that it stopped failing — that is a liveness problem this query cannot distinguish from a healthy silence, which is exactly why an operator dashboard should alert on the *absence* of rows here, not only their content.

**Which dependency failed** is logged, not returned — `ReadinessProbe.LogFailures` writes it once per probe, at Error, to the ordinary `ILogger<ReadinessProbe>` category, which lands in `traces` like any other log record:

```kusto
traces
| where timestamp > ago(1d)
| where severityLevel >= 3   // Error and above
| where message startswith "Readiness check"
| project
    timestamp,
    CheckName = tostring(customDimensions["CheckName"]),
    CheckStatus = tostring(customDimensions["CheckStatus"]),
    ElapsedMilliseconds = todouble(customDimensions["ElapsedMilliseconds"])
| order by timestamp desc
```

## A note on sampling and itemCount

Every telemetry item that can be sampled carries `itemCount`: a kept item you see represents itself **and** `itemCount − 1` others the sampler dropped, so `count()` always undercounts once sampling is active and `sum(itemCount)` is the corrected form. First, check whether sampling is even happening — if `RetainedPercentage` reads 100, every query above simplifies to plain counting:

```kusto
union requests, dependencies, exceptions, traces
| where timestamp > ago(1d)
| summarize RetainedPercentage = 100 / avg(itemCount) by bin(timestamp, 1h), itemType
```

Three things this API's configuration means for that number, all covered in [telemetry.md](telemetry.md):

- **`traces` should read 100.** `AzureMonitor:EnableTraceBasedLogsSampler` ships `false`, so the access log is a census independent of span sampling — if `traces` shows less than 100 here, something changed that setting.
- **`requests` and `dependencies` sample together, at `AzureMonitor:TracesPerSecond` (`5.0` by default), because OpenTelemetry samples a whole trace as a unit** — a kept `requests` row's `dependencies` children are always kept alongside it, never separately.
- **`customMetrics` is never sampled**, by Application Insights design, not by this app's configuration — which is why every recipe above that needs an exact number (token totals, tool-run counts) reaches for a metric instead of a span.

`sum(itemCount)` is exact for **counting occurrences** — requests, failures, runs. It is only an *estimate* for summing a value that varies per item (tokens, bytes, durations), because it assumes every dropped sibling matched the one kept row exactly; see the caveat under [LLM spend by model and by user](#llm-spend-by-model-and-by-user) for where that estimate is used deliberately, and why the metric-based alternative exists.

## Related docs

- [Telemetry](telemetry.md) — what is recorded, how it is registered, and why
- [Telemetry](telemetry.md) — the client-side half, and how it correlates to a server trace
- [Ingestion](../documents/ingestion.md) — the spreadsheet ceilings these metrics report on
- [Sheet Query](../documents/sheet-query.md) — the query-side counterpart
- [Summarization](../documents/summarization.md) — the summarization run metrics
