# Telemetry

The access log, the server's OpenTelemetry registration, and the browser SDK.

## The access log

`RequestLoggingMiddleware` writes four source-generated events, so a filtered-out record costs an
`IsEnabled` check and no allocation.

| Id | Event | Level | When |
| --- | --- | --- | --- |
| 1000 | `HttpRequestStarted` | `RequestStartLevel` (default Information) | A request arrives, before authentication |
| 1001 | `HttpRequestCompleted` | derived, see below | The pipeline returns, however it returned |
| 1002 | `HttpRequestPayload` | `Bodies:LogLevel` | Body capture is on and something was captured |
| 1003 | `HttpRequestPayloadCaptureFailed` | Warning | A body could not be read; the request is unaffected |
| 1004 | `HttpRequestLoggingFailed` | Warning | The access log itself threw; the request is unaffected |

### Fields worth knowing

- **`RequestPath` has its query values redacted** by default, keys intact:
  `/api/conversations/search?name=[redacted]&take=20`. A key names a parameter; a value is whatever
  the user typed. `RequestLogging:SafeQueryKeys` lists exceptions.
- **`UserId` is the Entra object id**, read straight off `context.User` rather than through
  `ITokenService`, which throws on an anonymous request — and an anonymous request is one of the
  cases most worth logging. It is null on the start record always, and on the completion record for
  anonymous callers.
- **`RouteTemplate` is null for an unrouted request**, which is how a 404 from routing is
  distinguishable from a 404 a handler chose to return.
- **`ResponseContentLength` is null for a streamed response.** The framework sets no length when it
  chunks, and the middleware deliberately does not count bytes itself.
- **`Outcome`** is `Completed`, `ClientAborted` or `Faulted`.

**No record carries a trace id.** The hosting layer already puts `TraceId` and `SpanId` on every log
scope, and that is the same W3C value `ProblemDetailsRegistration` writes into problem bodies as
`traceId` — so an id copied out of an error response already finds these records. Repeating it under
the same name with a different value would be worse than omitting it.

### Level selection

First match wins:

| Condition | Level |
| --- | --- |
| `Faulted` | Error |
| `ClientAborted` | Information |
| status >= 500 | Error |
| status >= 400 | Warning |
| elapsed > `SlowRequestThreshold` | Warning |
| otherwise | Information |

`ClientAborted` sits at Information on purpose and is checked *before* the status bands: a user
navigating away mid-stream is ordinary traffic on a chat platform, and warning on it would drown the
level that is supposed to mean "look at this".

The payoff is that severity carries the meaning — filtering the middleware category to Warning gives
a complete, self-maintaining error log.

### Excluded paths

`RequestLogging:ExcludedPaths` (default `/health`, `/openapi`, `/scalar`) matches with
`StartsWithSegments`, case-insensitively. A match suppresses the start record and skips body capture
entirely.

**A failure on an excluded path is still logged.** The completion record is written whenever an
exception escaped or the status is 400 or above. Silencing a health probe is worth doing; silencing
one that has started returning 500 is not.

### Position in the pipeline

```csharp
app.UseHttpsRedirection();
app.UseCors("AllowSpecificOrigins");
app.UseRequestLogging();      // here
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
```

Four consequences, all deliberate:

1. **Ahead of `UseExceptionHandler`, not inside it.** The handler writes the response and returns
   normally, so by the time `next()` comes back the status the client received is final — which is
   what the completion record should report. Registered inside the handler, every failure would look
   like an in-flight exception and would duplicate what the exception handlers already log.
2. **First in the app pipeline**, so it can call `EnableBuffering` before anything reads the body.
3. **Ahead of `UseAuthentication`**, so the caller is anonymous inbound. That is why `UserId` appears
   only on the completion record.
4. **CORS preflight `OPTIONS` never reaches it**, because `UseCors` short-circuits above. Intended
   noise reduction, not an oversight.

The middleware is convention-based, so it is constructed once when the pipeline is built and its
field initializers run once rather than per request — which is also why its options are validated at
startup.

## Body capture

**Off by default, and it should usually stay off.** This is a chat platform: request bodies carry
user prompts, response bodies carry model output and extracted document text. Turning capture on
puts user content into the configured telemetry backend, and it is **not selective** — every
eligible request is captured, not just failing ones.

```json
"RequestLogging": {
  "Bodies": { "LogRequestBody": true, "LogResponseBody": true, "LogLevel": "Debug" }
}
```

Setting `LogLevel` to `Debug` adds a second gate: capture then requires both the switch and a
category filter that admits Debug, making an accidental production capture two mistakes rather than
one.

| Key | Default | Meaning |
| --- | --- | --- |
| `Bodies:MaxBodyBytes` | 4096 | Byte cap per body, and the cutoff for whether a request body is read at all |
| `Bodies:AllowedContentTypes` | `application/json`, `application/problem+json`, `text/plain` | Eligible media types, compared with parameters stripped. Any media type with a `+json` structured suffix is eligible regardless of the list |

`JsonPropertyRedactor` blanks named properties before a captured body is written.

## Server telemetry

`TelemetryRegistration.AddEnterpriseTelemetry` registers the Azure Monitor OpenTelemetry distro,
bound to the `AzureMonitor` section. With no connection string configured the distro is skipped and
nothing is exported.

Names shared between projects live in `Enterprise.Gpt.Common/Observability/TelemetryNames.cs`.
`Enterprise.Gpt.Chat` is registered as both an `ActivitySource` and a `Meter`, so `ChatMetrics`
instruments export with no extra wiring. Instruments are recorded to whether or not anything is
exporting, which costs nothing and keeps the code path identical between environments.

Chat clients are wrapped with `UseOpenTelemetry()` as the **outermost** builder link, which is what
lets it record `gen_ai.conversation.id` before the innermost callback clears `ConversationId` for
the wire.

`EndUserEnrichingProcessor` attaches the caller to spans.

## Browser telemetry

`@microsoft/applicationinsights-web` is loaded only through `await import(...)`, behind an optional
`telemetry` block on `config.json` — so it sits in a lazy chunk of its own (~195 kB) and
`check-initial-chunk.mjs` polices that.

- **`enableAutoRouteTracking` is off.** The SDK's own route tracking fires on history changes that do
  not correspond to a screen the user reached; page views are raised from a `NavigationEnd`
  subscription instead.
- **URLs are scrubbed** before they are sent, on the same grounds as query redaction above.
- **Correlation** to the server trace rides the standard headers, so a browser page view and its
  server requests share a trace id.

**This connection string is public.** It ships in `config.json` and is readable by anyone who loads
the app, which has to shape where it points — an instrumentation key that also accepts server
telemetry should not be the one handed to browsers.

## Health

`GET|HEAD /health` and `/health/ready` are anonymous and mapped in every environment.
`/health/ready` runs cached readiness checks including `CosmosHealthCheck`.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLoggingMiddleware.cs` | The access log |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/RequestLogMessages.cs` | Source-generated events |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Middleware/JsonPropertyRedactor.cs` | Body redaction |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Observability/TelemetryRegistration.cs` | The Azure Monitor distro |
| `enterprise-gpt-api/Enterprise.Gpt.Common/Observability/TelemetryNames.cs` | Shared source and meter names |
| `enterprise-gpt-ui/src/app/core/telemetry/browser-telemetry.ts` | The lazy browser SDK |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/TestInfrastructure/RequestLoggingTestHarness.cs` | Access-log tests |

## Related

- [kql-cookbook.md](kql-cookbook.md)
- [../operations/configuration.md](../operations/configuration.md)
