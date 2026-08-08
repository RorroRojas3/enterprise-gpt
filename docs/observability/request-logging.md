# Request Logging and Application Insights

Reference for the API's access log: what [`RequestLoggingMiddleware`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLoggingMiddleware.cs) records about every request, where it sits in the pipeline and why, how the `RequestLogging` configuration section changes its behaviour, what body capture does to user content when you turn it on, and how [`TelemetryRegistration`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Observability/TelemetryRegistration.cs) gets those records — and the LLM spans the chat clients already emit — into Application Insights. Audience: engineers debugging a production request, and operators tuning telemetry volume.

## 1. Why this exists

Before this change the API could not tell you that a request had happened.

[`appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json) pins `"Microsoft.AspNetCore": "Warning"`, which suppresses ASP.NET Core's own request start/finish Information logs. That is a reasonable setting — those records are noisy and carry little — but nothing replaced them, so a healthy request produced no log line at all, and neither did most failures:

| What happened | What was logged before |
|---|---|
| Any successful request | nothing |
| A `403` from [`PermissionEndpointFilter`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs) | nothing — the filter writes a `ProblemDetails` and returns; it never throws |
| A `400` from [`MaxUploadSizeEndpointFilter`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/MaxUploadSizeEndpointFilter.cs) | nothing, for the same reason |
| A `401` from the authentication challenge | nothing |
| An unrouted `404` / a `405` | nothing |
| An exception | one line, `"An error occurred: {Message}"`, from one of the three [`IExceptionHandler`s](../../enterprise-gpt-api/Enterprise.Gpt.Api/ExceptionHandlers) — with no method, path, status, duration or caller |

Two consequences followed. Ordinary triage questions — *which* endpoint is slow, *who* is getting the 403, *how often* — had no answer. And a large class of failures, the ones that return a problem response without raising an exception, were completely invisible: the API told the client exactly what went wrong and told the operator nothing.

Separately, [`Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) already called `.UseOpenTelemetry()` on both chat clients, so every LLM turn produced a GenAI span with token counts on it. Nothing was listening. The spans were created and dropped.

This feature closes both gaps, and keeps them independent of each other: **the middleware writes through `ILogger<T>` and names no backend**, so §7 is the only place Application Insights appears and swapping it for Serilog is a one-file change (§10.1).

## 2. Quick start

**Locally, nothing to do.** Request logging is on by default and writes to whatever providers the host has — the console, under `dotnet run`:

```text
info: Enterprise.Gpt.Api.Middleware.RequestLoggingMiddleware[1000]
      HTTP GET /api/models started (contentType: (null), contentLength: (null), userAgent: Mozilla/5.0 ..., clientIp: (null))
info: Enterprise.Gpt.Api.Middleware.RequestLoggingMiddleware[1001]
      HTTP GET /api/models responded 200 in 34.7 ms (route: api/models, endpoint: HTTP: GET api/models, user: 8f14…, outcome: Completed, responseContentLength: 1284)
```

**To send those records to Application Insights**, configure a connection string. Nothing else changes:

```bash
# local development — user secrets, never appsettings.json
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets set "AzureMonitor:ConnectionString" "InstrumentationKey=…;IngestionEndpoint=…"
```

In a deployed environment set the `APPLICATIONINSIGHTS_CONNECTION_STRING` environment variable instead; App Service sets it for you when an Application Insights resource is linked. With neither configured the exporter is not registered at all (§7.1).

**To quieten the log in production**, filter the category — it sits outside the `Microsoft.AspNetCore` prefix precisely so it can be tuned on its own:

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning",
    "Enterprise.Gpt.Api.Middleware": "Warning"
  }
}
```

That keeps failures and slow requests (both Warning or above) and drops the rest. §8 covers the other levers.

## 3. What is recorded

Four events, all on the category `Enterprise.Gpt.Api.Middleware.RequestLoggingMiddleware`, all source-generated in [`RequestLogMessages`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLogMessages.cs) so a filtered-out record costs an `IsEnabled` check and no allocation.

| Id | Event name | Level | When |
|---|---|---|---|
| 1000 | `HttpRequestStarted` | `RequestLogging:RequestStartLevel` (default Information) | a request arrives, before authentication |
| 1001 | `HttpRequestCompleted` | derived — see §3.2 | the pipeline returns, however it returned |
| 1002 | `HttpRequestPayload` | `RequestLogging:Bodies:LogLevel` | only when body capture is on and something was captured (§6) |
| 1003 | `HttpRequestPayloadCaptureFailed` | Warning | a request body could not be read for logging; the request itself is unaffected |
| 1004 | `HttpRequestLoggingFailed` | Warning | the access log itself threw; the request is unaffected (§6.6) |

### 3.1 Fields

**1000 `HttpRequestStarted`** — `RequestMethod`, `RequestPath`, `RequestContentType`, `RequestContentLength`, `UserAgent`, `ClientIp`.

**1001 `HttpRequestCompleted`** — `RequestMethod`, `RequestPath`, `StatusCode`, `ElapsedMilliseconds`, `RouteTemplate`, `EndpointName`, `UserId`, `Outcome`, `ResponseContentLength`, plus the exception when one escaped.

Details that matter when you read them:

- **`RequestPath` has its query values redacted** by default, keys intact: `/api/conversations/search?name=[redacted]&take=20`. A key names a parameter; a value here is whatever the user typed. `RequestLogging:SafeQueryKeys` lists the exceptions (§5.1).
- **`UserId` is the Entra object id (`oid`)**, read straight off `context.User` rather than through `ITokenService`, which throws on an anonymous request — and an anonymous request is one of the cases most worth logging. It is `null` on the start record always (§4) and on the completion record for anonymous callers.
- **`RouteTemplate` is `null` for an unrouted request**, which is how a 404 from routing is distinguishable from a 404 a handler chose to return.
- **`ResponseContentLength` is `null` for a streamed response.** The framework sets no length when it chunks, and the middleware deliberately does not count bytes itself.
- **`Outcome` is one of `Completed`, `ClientAborted` or `Faulted`** — `Faulted` when an exception escaped the pipeline, `ClientAborted` when `RequestAborted` was signalled, `Completed` otherwise, whatever the status code.

**No record carries a trace id.** The hosting layer already puts `TraceId` and `SpanId` on every log scope, and that is the same 32-hex W3C value [`ProblemDetailsRegistration`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemDetailsRegistration.cs) writes into problem bodies as `traceId` — so an id copied out of an error response already finds these records (§7.2). Repeating it under the same name with a different value would be worse than omitting it.

### 3.2 Level selection on completion

Evaluated in this order; the first match wins:

| Condition | Level |
|---|---|
| `Outcome` is `Faulted` | **Error** |
| `Outcome` is `ClientAborted` | **Information** |
| status ≥ 500 | **Error** |
| status ≥ 400 | **Warning** |
| elapsed > `SlowRequestThreshold` | **Warning** |
| otherwise | **Information** |

`ClientAborted` sits at Information on purpose, and is checked *before* the status bands: a user navigating away mid-stream is ordinary traffic on a chat platform, and already maps to a bodiless 499 in `OperationCanceledExceptionHandler`. Warning on it would drown the level that is supposed to mean "look at this".

The practical payoff is that severity carries the meaning. `Enterprise.Gpt.Api.Middleware: Warning` is a complete, self-maintaining error log — every 4xx, every 5xx, every fault and every slow request, and nothing else.

### 3.3 Excluded paths

`RequestLogging:ExcludedPaths` (default `/health`, `/openapi`, `/scalar`) is matched with `StartsWithSegments`, case-insensitively. A match suppresses the start record and skips body capture entirely — buffering a body only to discard it is work nobody asked for.

**A failure on an excluded path is still logged.** The completion record is written whenever an exception escaped or the status is ≥ 400. Silencing a health probe is worth doing; silencing a health probe that has started returning 500 is not.

There is no `/health` endpoint in this build. The default anticipates one; `/openapi` and `/scalar` are mapped in Development only.

## 4. Where it sits in the pipeline

```csharp
app.UseHttpsRedirection();
app.UseCors("AllowSpecificOrigins");
app.UseRequestLogging();      // ← here
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
```

Four consequences, all deliberate:

1. **Ahead of `UseExceptionHandler`, not inside it.** The handler writes the response and returns normally, so by the time `next()` comes back the status the client received is already final — which is exactly what the completion record should report. Registered *inside* the handler instead, every failure would look like an in-flight exception and would duplicate what the three exception handlers already log.
2. **First in the app pipeline**, so it can call `EnableBuffering` before anything reads the request body (§6.2).
3. **Ahead of `UseAuthentication`**, so the caller is anonymous on the way in. This is why `UserId` appears only on the completion record: on the way out, `context.User` has been populated.
4. **CORS preflight `OPTIONS` requests never reach it.** `UseCors` short-circuits them above. That is intended noise reduction, not an oversight — the browser sends one per cross-origin route.

The middleware is convention-based (`public sealed class` with `InvokeAsync`), so it is constructed once when the pipeline is built. Its `HashSet` and `PathString[]` field initialisers therefore run once, not per request — which is also why the options are validated at startup (§5.3).

## 5. Configuration

The `RequestLogging` section binds to [`RequestLoggingOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLoggingOptions.cs).

### 5.1 Settings

```json
"RequestLogging": {
  "Enabled": true,
  "RequestStartLevel": "Information",
  "SlowRequestThreshold": "00:00:03",
  "RedactQueryValues": true,
  "IncludeUserAgent": true,
  "IncludeClientIp": false,
  "Bodies": {
    "LogRequestBody": false,
    "LogResponseBody": false,
    "LogLevel": "Information",
    "MaxBodyBytes": 4096
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | When false the middleware is left **out of the pipeline** rather than short-circuiting inside itself, so a disabled access log costs nothing per request. Read once, at startup — toggling it needs a restart |
| `RequestStartLevel` | `Information` | Level for event 1000. Lowering it to `Debug` roughly halves the volume and loses little; the completion record carries a superset of these fields. It earns Information for the two cases the completion record cannot cover — a request still running (an SSE stream lives for minutes) and one whose process died before it finished |
| `SlowRequestThreshold` | `00:00:03` | A request that gets past the status bands and takes longer than this is raised to Warning |
| `RedactQueryValues` | `true` | Replace query values with `[redacted]`. On by default because query values here are user content |
| `SafeQueryKeys` | `skip`, `take`, `page`, `pageSize`, `sort`, `order` | Keys whose values survive redaction, compared case-insensitively |
| `ExcludedPaths` | `/health`, `/openapi`, `/scalar` | Prefixes whose *successful* requests are not logged (§3.3) |
| `IncludeUserAgent` | `true` | Log the `User-Agent` header |
| `IncludeClientIp` | `false` | Log the remote IP. **Off by default: an IP address is personal data under the GDPR**, and the Entra object id on the completion record identifies the caller far more usefully |
| `Bodies:*` | see §6 | Payload capture, off by default |

### 5.2 Configuring a list replaces the default — it does not extend it

**The one thing to know before editing this section.** Every list property (`ExcludedPaths`, `SafeQueryKeys`, `Bodies:AllowedContentTypes`, `Bodies:ExcludedPaths`, `Bodies:RedactedPropertyNames`) defaults to **empty in code** and is filled in by a `PostConfigure` step after binding. That is why none of them appears in `appsettings.json`.

The reason is `ConfigurationBinder`: it binds a collection *into* the existing instance rather than replacing it. An inline default would therefore make configuration purely **additive** — an operator narrowing `AllowedContentTypes` to a single media type would silently keep all the others, and an allow-list you cannot narrow is not an allow-list.

So, for an operator:

```json
"RequestLogging": {
  "ExcludedPaths": [ "/health", "/openapi", "/scalar", "/metrics" ]
}
```

`/metrics` alone would **replace** the three defaults, not join them. List them all, every time.

### 5.3 Startup validation

[`AddRequestLogging`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLoggingRegistration.cs) validates with `ValidateOnStart`, so bad configuration fails the app rather than every request:

| Rule | Message |
|---|---|
| `SlowRequestThreshold > 0` | `RequestLogging:SlowRequestThreshold must be greater than zero.` |
| `RequestStartLevel` is a real level | `RequestLogging:RequestStartLevel must be a valid log level.` |
| `Bodies:LogLevel` is a real level | `RequestLogging:Bodies:LogLevel must be a valid log level.` |
| `Bodies:MaxBodyBytes` in 1…1 048 576 | `RequestLogging:Bodies:MaxBodyBytes must be between 1 and 1048576.` |
| every list entry non-empty, every path rooted at `/` | `RequestLogging list entries must be non-empty, and every path must start with '/'.` |

The alternatives are all worse. A non-positive threshold silently marks every request slow. An unrooted path or a `null` list entry throws from the middleware's own field initialisers — which run when the pipeline is built, and surface as a container activation failure rather than as a message naming the setting. The `MaxBodyBytes` rule is spelled out in code as well as carrying a `[Range]` attribute because `ValidateDataAnnotations` only inspects the root object; an attribute on a nested options type is documentation only.

## 6. Body capture

**Off by default, and it should usually stay off.** This is a chat platform: request bodies carry user prompts, response bodies carry model output and extracted document text. Turning capture on puts user content into whatever telemetry backend is configured, and it is not selective — **every** eligible request is captured, not just failing ones.

When it is on, the rest of the design exists so that it cannot break streaming, exhaust memory, or ship a 50 MB upload to Application Insights.

### 6.1 Switching it on

```json
"RequestLogging": {
  "Bodies": {
    "LogRequestBody": true,
    "LogResponseBody": true,
    "LogLevel": "Debug"
  }
}
```

Setting `LogLevel` to `Debug` adds a second gate: capture then requires both the switch and a category filter that admits Debug, which makes an accidental production capture two mistakes rather than one.

The remaining knobs:

| Key | Default | Meaning |
|---|---|---|
| `Bodies:MaxBodyBytes` | `4096` | Byte cap per body. Also the cutoff for whether a request body is read at all (§6.2) |
| `Bodies:AllowedContentTypes` | `application/json`, `application/problem+json`, `text/plain` | Media types eligible for capture, compared with parameters stripped (`application/json; charset=utf-8` matches). Any media type with a `+json` structured suffix is eligible regardless of the list |
| `Bodies:ExcludedPaths` | `/api/conversations`, `/api/documents` | Paths never captured. `*` matches exactly one segment; a pattern matches any path it prefixes |
| `Bodies:RedactedPropertyNames` | `password`, `token`, `accessToken`, `refreshToken`, `idToken`, `secret`, `apiKey`, `clientSecret`, `connectionString`, `authorization` | Property names whose values are replaced (§6.4) |

Remember §5.2: setting any of these replaces the default list.

Two things about those defaults. The allow-list is what keeps binary payloads out — `multipart/form-data` uploads and `application/octet-stream` downloads are simply not on it — and an allow-list is used rather than a deny-list because the failure modes are asymmetric: a media type wrongly excluded costs a missing log field, one wrongly included ships a binary upload into the telemetry backend. And `/api/conversations` is excluded *as a whole*, not just its stream route, because `GET api/conversations/{id}/messages` returns the entire prompt-and-response transcript as ordinary JSON and the list routes return user-authored titles.

`Bodies:ExcludedPaths` takes path patterns rather than route templates because capture has to be arranged **before** `next()` runs, and routing has not resolved an endpoint at that point. `api/conversations/{id}/stream` is not knowable yet; `/api/conversations/*/stream` is.

### 6.2 Request bodies

A request body is captured when capture is on, the path is not excluded, the content type is on the allow-list, and `Content-Length` is present and within `MaxBodyBytes`.

**An oversized body is reported, not read.** The record carries `[skipped: 2097152 bytes exceeds RequestLogging:Bodies:MaxBodyBytes of 4096]` and the `Truncated` field is set. The reason is `EnableBuffering`: it spools the *entire* body — to disk past its own threshold — however few bytes are ultimately wanted, so truncating a large request would cost the whole upload's worth of I/O to log four kilobytes of it.

**A chunked request is skipped** for the same reason: it declares no length, so its size is unknown until it has been read.

The read itself rents from `ArrayPool<byte>` rather than allocating — the validated cap runs to 1 MB, and a plain array above 85 KB is a large object heap allocation on every request carrying a body — and `Request.Body.Position` is reset in a `finally`, outside the `try`, so a read that threw part-way still leaves the endpoint a complete payload to model-bind.

### 6.3 Response bodies, and why streaming survives

[`ResponseCaptureStream`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/ResponseCaptureStream.cs) is a **pass-through**, not a buffer. Every write and every flush reaches the real stream immediately and the copy is taken afterwards. A wrapper that held bytes back until completion would turn `POST api/conversations/{id}/stream` — which writes one model fragment and flushes, over and over — into a single delivery at the end.

Eligibility is decided on the **first write**, not at construction, because a response's content type is not known until the handler sets it. A `text/event-stream` response therefore stops being captured the moment it identifies itself, and the wrapper degrades to a plain forwarder for the rest of its life. Combined with `/api/conversations` being on `Bodies:ExcludedPaths`, that is two independent reasons SSE is never captured.

The capture buffer starts at 4 KB and doubles towards `MaxBodyBytes`, so a small response does not pay for a large cap. Capture stays armed once the buffer is full rather than switching itself off, so a later write arriving after the cap is what sets `Truncated`.

[`ResponseCaptureBodyFeature`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/ResponseCaptureBodyFeature.cs) swaps the stream into `IHttpResponseBodyFeature` and leaves everything else with the server's own feature. It is written out rather than using the framework's `StreamResponseBodyFeature`, whose `CompleteAsync` completes only its own pipe writer and never tells the prior feature the response is finished — so a handler calling `HttpResponse.CompleteAsync()` while the wrapper was installed would not reach Kestrel. `SendFileAsync` deliberately bypasses the capture stream and goes straight to the server, preserving the zero-copy `sendfile` path; the cost is that file responses are not captured, which is no loss, since they are binary and would fail the allow-list anyway.

Before the original feature is put back, anything still sitting in the wrapper's pipe writer is flushed to the connection — a handler that wrote through `HttpResponse.BodyWriter` without flushing would otherwise lose those bytes, because the host goes on to complete a feature that knows nothing about that writer. The flush is skipped once the caller has disconnected: there is nobody left to deliver to.

### 6.4 Redaction

[`IRequestBodyRedactor`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/JsonPropertyRedactor.cs) is the extension point, and it is the seam `ILogger` cannot provide: which backend receives a record is a hosting concern, but *what the record is allowed to contain* has to be decided before the record exists. Register your own implementation before `AddRequestLogging` runs and the default is left alone (it is registered with `TryAddSingleton`).

The default `JsonPropertyRedactor` rewrites JSON, replacing the value of every property named in `RedactedPropertyNames`:

```json
{"clientId":"acme","clientSecret":"[redacted]","scopes":["read"]}
```

- Nested objects and array elements are walked; a non-string value (a number, an object) is replaced with the string marker just the same.
- Names are matched **case-insensitively and ignoring `_` and `-`**, so `accessToken`, `access_token` and `Access-Token` are one name. OAuth and OIDC payloads are snake_case while the rest of this API is camelCase, and a list that had to spell out both would quietly miss whichever spelling nobody thought of.
- Matching is on the whole property name, so a property merely *containing* a listed name is preserved.

**Content that cannot be parsed as JSON is dropped wholesale** if it mentions a sensitive name — replaced with `[redacted: unparseable payload naming a sensitive property]`. This arm is not rare: a payload cut at the byte cap is nearly always invalid JSON. Falling back to the raw text would defeat the point of having a redactor, and masking in place would need a parser for a format that is, by definition, not parseable here. Non-JSON content types take the same path.

### 6.5 Log injection

Control characters are stripped (replaced with spaces) from the path, from query keys and values, from the user agent, and from every captured body. `Request.Path.Value` is percent-*decoded*, so a request for `/api/x%0A…` would otherwise put a real newline into the record and forge a second line in any text-based sink — [CWE-117](https://cwe.mitre.org/data/definitions/117.html).

### 6.6 Logging never fails a request

The completion and payload writes sit in a `try`/`catch` inside the `finally`, and a failure there is reported as event 1004 rather than thrown. An exception leaving a `finally` **replaces** whatever was already propagating, so an unlucky logger or a custom redactor could turn a working request into a 500 *and* hide the real fault on the way. Nothing the access log does is worth that.

The same posture applies at every other edge: a body that could not be read becomes event 1003 and a missing log field, the stream is still rewound, and a failure while flushing the capture wrapper is reported rather than raised. `RequestLoggingResilienceTests` pins each of these, including that a throwing redactor does not disturb an exception the pipeline was already reporting.

## 7. Application Insights

### 7.1 Registration

[`TelemetryRegistration.AddEnterpriseTelemetry`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Observability/TelemetryRegistration.cs) is the only place in the API that names a telemetry backend:

```csharp
services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("enterprise-gpt-api"))
    .UseAzureMonitor(options => options.ConnectionString = connectionString)
    .WithTracing(tracing => tracing.AddSource(ChatTelemetrySourceName))
    .WithMetrics(metrics => metrics.AddMeter(ChatTelemetrySourceName));
```

The package is `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0 — the OpenTelemetry distro, not the classic `Microsoft.ApplicationInsights.AspNetCore` SDK. The distro is [Microsoft's current guidance for new ASP.NET Core applications](https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-enable?tabs=aspnetcore), the two must not coexist, and only the distro exports the `UseOpenTelemetry()` spans the chat clients already emit.

The connection string is read from `APPLICATIONINSIGHTS_CONNECTION_STRING` first, then `AzureMonitor:ConnectionString`. It carries an ingestion key, so it is treated as a secret: an environment variable in deployed environments, user secrets locally, **never `appsettings.json`**. Authentication is by connection string only — no `Credential`/`DefaultAzureCredential` is configured, so moving to Entra-authenticated ingestion is a change here.

**With no connection string configured the distro is not registered at all.** Not registered with nowhere to send to — skipped entirely. That is what keeps local runs and the integration test host free of an exporter retrying in the background, and it means "I see no telemetry" has exactly one first thing to check.

Note that `UseAzureMonitor()` may be called only once per `IServiceCollection`; a second call throws `NotSupportedException`.

### 7.2 What lands where, and how it correlates

`UseAzureMonitor` registers an OpenTelemetry `ILoggerProvider` alongside the trace and metric exporters. So:

| Signal | Application Insights table | Source |
|---|---|---|
| Access log records (1000–1004) | `traces` | the middleware, via `ILogger` |
| Incoming HTTP requests | `requests` | the distro's ASP.NET Core instrumentation |
| Outgoing HTTP and SQL calls | `dependencies` | the distro's HttpClient and SqlClient instrumentation |
| LLM turns | `dependencies` | `Enterprise.Gpt.Chat` (§7.3) |
| `gen_ai.client.token.usage` and the rest | `customMetrics` | `Enterprise.Gpt.Chat` |

Everything for one request shares an `operation_Id`, and that is **the same 32-hex W3C trace id the API returns as `traceId` on every problem response**. A user reporting an error can quote the `traceId` from the response body, and this query finds the whole request:

```kusto
union traces, requests, dependencies, exceptions
| where operation_Id == "a1b2c3d4e5f60718293a4b5c6d7e8f90"
| order by timestamp asc
```

That correlation is why the middleware's own templates carry no trace-id field (§3.1): the hosting layer already puts one on the log scope, and a second one under the same name with a different value is precisely the defect to avoid. The cloud role name is `enterprise-gpt-api`, set through `ConfigureResource`.

### 7.3 LLM spans and token metrics

Both chat clients now name their telemetry source explicitly:

```csharp
.UseOpenTelemetry(sourceName: TelemetryRegistration.ChatTelemetrySourceName)   // "Enterprise.Gpt.Chat"
```

Named at the producer and the consumer rather than relying on the library's default, because the two must agree for the spans to be exported — and a default that changes with a package bump would take the LLM traces off the map with no build error.

`Enterprise.Gpt.Chat` is registered as **both a source and a meter**, because `Microsoft.Extensions.AI`'s `OpenTelemetryChatClient` builds an `ActivitySource` and a `Meter` from the same name. Registering only the source would have exported the spans and dropped `gen_ai.client.token.usage` — the metric that turns LLM spend into something observable.

### 7.4 Sampling — check this before relying on the access log

The Azure Monitor distro applies **rate-limited sampling by default, capped at 5 traces per second**. Under load that is well below this API's request rate, so the `requests` and `dependencies` you see are a sample, not a census.

Sampling for ASP.NET Core is [not configurable from a configuration file](https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-configuration#enable-sampling). Change it in `AddEnterpriseTelemetry`:

```csharp
.UseAzureMonitor(options =>
{
    options.ConnectionString = connectionString;
    options.TracesPerSecond = 20.0;   // or: options.SamplingRatio = 0.5F; options.TracesPerSecond = null;
})
```

or with environment variables, which take precedence over code:

```bash
OTEL_TRACES_SAMPLER=microsoft.rate_limited
OTEL_TRACES_SAMPLER_ARG=20
```

Two things follow. **Metrics are never sampled**, so alert on `customMetrics` and the standard request metrics rather than on span counts. And whether the `traces` records follow a span's sampling decision is governed by `AzureMonitorOptions.EnableTraceBasedLogsSampler`, which this build leaves at the distro's default — Microsoft documents that default as varying by language and distro version, so confirm it against the version you deploy before treating the access log as complete.

## 8. Operating it

| Goal | Do this | Takes effect |
|---|---|---|
| Keep only failures and slow requests | `"Enterprise.Gpt.Api.Middleware": "Warning"` under `Logging:LogLevel` | immediately with a reloading configuration provider |
| Halve the volume, keep every outcome | `"RequestLogging:RequestStartLevel": "Debug"` | restart |
| Stop logging a new noisy path | add it to `RequestLogging:ExcludedPaths` — **and re-list the defaults** (§5.2) | restart |
| Turn the middleware off entirely | `"RequestLogging:Enabled": false` | restart — it is omitted from the pipeline, not short-circuited |
| Capture payloads for a debugging session | §6.1, and turn it back off afterwards | restart |

The category filter is the lever to reach for first. It is the only one that does not need a restart, and because severity carries the meaning (§3.2), `Warning` yields a complete error log rather than an arbitrary subset.

## 9. Testing

Unit tests in [`tests/Enterprise.Gpt.Unit.Test/Middleware/`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Middleware) — xUnit v3, driving the middleware over a `DefaultHttpContext` through a shared `RequestLoggingTestHarness`, with `Microsoft.Extensions.Diagnostics.Testing` 10.8.0 (`FakeLogCollector`) as the sink:

| File | Covers |
|---|---|
| `RequestLoggingMiddlewareTests.cs` | Start and completion records; the level table as a theory over status codes; slow-request promotion either side of the threshold; a throwing pipeline logged and rethrown; client abort at Information; excluded paths silent on success and loud on failure; the Entra oid present and anonymous callers not throwing; route template and endpoint name, present and absent; query redaction on, off, and with a safe key; client IP on and off; a response that has already started |
| `RequestLoggingMiddlewareBodyTests.cs` | JSON request capture with redaction; the stream rewound for model binding; multipart refused; a `charset` parameter tolerated; an oversized body reported without being read; wildcard path exclusion; response capture, truncation, and the exact-fill edge; **SSE still delivered fragment by fragment and not captured**; octet-stream not captured; the original feature restored on both the normal and the throwing path |
| `RequestLoggingResilienceTests.cs` | A throwing redactor failing neither the request nor an in-flight exception; a failed body read still rewinding; chunked requests skipped; control characters neutralised in paths and query keys; `UseRequestLogging` adding or omitting the middleware; and the options rules — invalid configuration failing, and a configured list **replacing** rather than extending the default |
| `JsonPropertyRedactorTests.cs` | Top-level, nested and in-array redaction; case and snake_case variants; a name merely contained preserved; non-string values; `problem+json`; truncated JSON dropped whole when sensitive and kept when not; plain text; empty body; no configured names |
| `BodyCapturePolicyTests.cs` | Content-type allow-list matching; prefix and wildcard path patterns; empty pattern list and empty path |

Integration tests in [`tests/Enterprise.Gpt.Integration.Test/Middleware/`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/Middleware) (`[Trait("Category", "Integration")]`, needs Docker) run against the real host, where the swapped `IHttpResponseBodyFeature` has a genuine prior feature behind it — the pipe writer, the completion handshake and the restore all run for real, which a `DefaultHttpContext` cannot show:

- `RequestLoggingIntegrationTests` — an authenticated success logged at Information with route and caller; an anonymous 401 and a filter-issued 403 both logged at Warning *(the two cases that produced nothing at all before this feature)*; an unrouted path logged with no route template; and no payload record when bodies are off.
- `ResponseBodyCaptureIntegrationTests` — runs on its own host with capture switched on: the response reaches the client intact and is logged, a problem body is captured without being disturbed, and a captured request body still model-binds.

[`CustomWebApplicationFactory`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/CustomWebApplicationFactory.cs) registers `AddFakeLogging` **filtered to the middleware category**, and exposes `GetRequestLogs()` / `ClearRequestLogs()`. The filter matters: an unfiltered collector accumulates every record the whole suite produces — EF Core included — for the lifetime of the shared fixture, which is a slow memory leak in exchange for records no test reads.

```bash
# from enterprise-gpt-api/
dotnet test --filter "Category!=Integration"   # unit only
dotnet test                                    # everything; Docker must be running
```

At the time of writing the unit suite passes in full, and one integration test fails — `ModelEndpointsIntegrationTests.GetModel_SeededModel_ReturnsModel`, which is pre-existing at `HEAD` and unrelated to this feature.

## 10. Known limits

### 10.1 The backend is replaceable; the middleware is not the thing to change

The middleware uses `ILogger<T>` and nothing else. Application Insights, Serilog and the console are all just providers registered at the host. **Moving to Serilog means replacing `Observability/TelemetryRegistration.cs` — no middleware code changes.** That is the whole reason redaction lives behind `IRequestBodyRedactor` in the middleware rather than in a sink: what a record may contain has to be decided before the record exists, so it cannot be delegated to whichever backend happens to be configured.

### 10.2 Everything else

- **Body capture is all-or-nothing.** There is no "capture only failures" mode: eligibility is settled before `next()` runs, and the status is not known until after. Enabling capture to chase one bad request captures every good one alongside it.
- **`Enabled`, and every other option, is read at startup.** Options are injected as `IOptions<T>`, not `IOptionsMonitor<T>`, so changes need a restart. The log-level filter (§8) is the only knob that reloads.
- **The `SafeQueryKeys` default is generic.** It covers pagination and sorting. A new endpoint with a non-sensitive query parameter — a status filter, say — logs `[redacted]` until the key is added, and adding it means re-listing the whole default (§5.2).
- **`ResponseContentLength` is `null` for anything chunked**, which is every streamed response, so it is not a usable field for bandwidth accounting.
- **A truncated capture can end mid-UTF-8-sequence** and decode to a replacement character. Cutting on a character boundary would mean decoding incrementally on the write path — real work on every response, to tidy up the last glyph of a payload already marked `Truncated`.
- **The excluded-path defaults name endpoints that do not all exist.** There is no `/health` route in this build, and `/openapi` and `/scalar` are Development-only.
- **Nothing yet consumes the log.** There are no Application Insights alert rules, no workbook and no saved queries checked into this repository; §7.2 is a starting point, not a dashboard.

## 11. Key files

| Concern | File |
|---|---|
| The middleware | [`Api/Middleware/RequestLoggingMiddleware.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLoggingMiddleware.cs) |
| Options and defaults | [`Api/Middleware/RequestLoggingOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLoggingOptions.cs) |
| Log message definitions | [`Api/Middleware/RequestLogMessages.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLogMessages.cs) |
| Capture eligibility | [`Api/Middleware/BodyCapturePolicy.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/BodyCapturePolicy.cs) |
| Response capture | [`ResponseCaptureStream.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/ResponseCaptureStream.cs), [`ResponseCaptureBodyFeature.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/ResponseCaptureBodyFeature.cs) |
| Redaction | [`Api/Middleware/JsonPropertyRedactor.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/JsonPropertyRedactor.cs) |
| DI and pipeline registration | [`Api/Middleware/RequestLoggingRegistration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLoggingRegistration.cs) |
| Application Insights | [`Api/Observability/TelemetryRegistration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Observability/TelemetryRegistration.cs) |
| Wiring and pipeline order | [`Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| Defaults shipped | [`Api/appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json) |
| `traceId` on problem responses | [`Api/Problems/ProblemDetailsRegistration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemDetailsRegistration.cs) |
| Unit tests | [`tests/Enterprise.Gpt.Unit.Test/Middleware/`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Middleware) |
| Integration tests | [`tests/Enterprise.Gpt.Integration.Test/Middleware/`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/Middleware) |
