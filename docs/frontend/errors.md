# Error Handling

`core/errors/` is a total, framework-agnostic layer that normalizes any thrown value into one
discriminated `AppError`. Every surface renders from that union; nothing downstream inspects an
`HttpErrorResponse`.

## The pieces

| File | Role |
| --- | --- |
| `app-error.ts` | The `AppError` union and `AppErrorBase` (`status`, `title`, `detail`, `traceId`, `instance`, `url`, `message`), plus `isProblemAppError` |
| `problem-types.ts` | The API's app-specific RFC 9457 type URIs, mirroring `Api/Problems/ProblemTypes.cs`. Opaque identifiers, matched verbatim, never resolved |
| `problem-details.ts` | The `ProblemDetails` shape as this API writes it, `parseProblemDetails`, and typed extension readers |
| `build-app-error.ts` | Selects the arm from `{status, problem, url, cause}`; also exports `abortedAppError`, `clientAppError`, `networkAppError` |
| `to-app-error.ts` | The total synchronous entry point for any thrown value |
| `to-app-error-from-response.ts` | Normalizes a failed raw-`fetch` `Response` |
| `to-app-error-from-blob.ts` | Async variant for `responseType: 'blob'` |
| `camel-case.ts` | Reimplements `JsonNamingPolicy.CamelCase` |
| `server-messages.ts` | `serverMessagesFor(error, field)` |
| `error-message.ts` | Arm to user-facing copy, plus the `NOTIFIABLE_KINDS` table |
| `retry-policy.ts` | `DEFAULT_RETRY_POLICY` and its injection token |

## Details that are easy to get wrong

**`toAppError` takes an optional `AbortSignal`.** `abort(reason)` rejects with the *reason*, not an
`AbortError`, so without the signal a deliberate stop is misclassified as a failure.

**`toAppErrorFromResponse` types its input structurally as `ResponseLike`**, not with `instanceof
Response`, because `instanceof` is unreliable across jsdom and Node realms.

**`toAppErrorFromBlob` exists because `HttpClient` hands back an unparsed `Blob`** on a
`responseType: 'blob'` request. Without it every typed problem on a download degrades to the plain
`http` arm and the reader loses the reason.

**`camel-case.ts` is a reimplementation, not a heuristic.** Validation `errors` keys are PascalCase
property names and form fields are camelCase, and .NET's algorithm lower-cases a leading run of
capitals — `IOStream` becomes `ioStream`, not `iOStream`. A naive `toLowerCase` on the first
character silently fails to match those fields.

**`serverMessagesFor` feeds a declarative `validate` rule**, because Signal Forms has no
`setErrors`. Server messages surface by the form reading store state, not by anything pushing into
the form.

**`NOTIFIABLE_KINDS` is `satisfies`-checked**, so adding an arm without deciding whether it deserves
a toast is a compile error. Cancellations and validation errors deliberately do not toast — one is
what the user asked for and the other is already rendered beside the field.

## Auth and retry

`authErrorDecision` is the single source of truth for whether a failure may trigger a token refresh.
**Only a bare 401 may.** The exhaustive `satisfies` table makes adding an arm without deciding a
compile error, so a new problem type cannot quietly become a refresh trigger.

`retryInterceptor` retries **GETs only**, on 502/503/504 and transport failures, with full-jitter
backoff. It never retries a 409, and never an app-typed 503 — a deployment that has not configured a
renderer or a provider will answer the same way to every attempt.

Interceptor order in `app.config.ts` is `[retryInterceptor, authInterceptor]` and is load-bearing:
retry is outermost, so each retried attempt re-enters auth and carries a fresh token.

## Rendering an error

Every arm carries `traceId`, which surfaces have to render — it is the only thing that ties a user's
report to a server log line.

Because problem-type URIs are matched verbatim, adding a type on the server means adding an arm
here. The two files are a pair.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-ui/src/app/core/errors/to-app-error.ts` | The total entry point |
| `enterprise-gpt-ui/src/app/core/errors/problem-types.ts` | The mirror of the server catalog |
| `enterprise-gpt-ui/src/app/core/errors/camel-case.ts` | .NET's camel-casing algorithm |
| `enterprise-gpt-ui/src/app/core/http/interceptors/retry.interceptor.ts` | GET-only retry with jitter |
| `enterprise-gpt-ui/src/app/core/auth/` | `authErrorDecision` and the bearer interceptor |
| `enterprise-gpt-ui/src/app/core/errors/to-app-error.spec.ts` | Arm selection across every input shape |

## Related

- [../architecture/backend.md](../architecture/backend.md) — the server side of the same catalog
- [state.md](state.md)
