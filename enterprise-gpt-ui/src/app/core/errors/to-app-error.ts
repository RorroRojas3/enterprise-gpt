import { HttpErrorResponse } from '@angular/common/http';
import { AppError } from './app-error';
import { abortedAppError, buildAppError, clientAppError, networkAppError } from './build-app-error';
import { parseProblemDetails } from './problem-details';

/** Context a caller can supply to classify a failure it has extra knowledge about. */
export interface ToAppErrorOptions {
  /**
   * The signal the caller passed to `fetch`.
   *
   * `AbortController.abort()` raises an `AbortError`, but `abort(reason)` — the
   * idiomatic way to record *why* a turn was stopped — rejects with the reason
   * itself, which can be any value including a plain string. Supplying the signal
   * is what lets a deliberate stop be recognized whatever the reason was.
   */
  readonly signal?: AbortSignal;
  /** The request URL, when the thrown value does not carry one. */
  readonly url?: string;
}

/**
 * Normalizes any thrown value into one {@link AppError}.
 *
 * Total and synchronous: it never throws and never returns a promise. Reading a
 * `fetch` body is asynchronous, so responses whose problem body still has to be
 * read go through `toAppErrorFromResponse` instead; handed a bare `Response`,
 * this function still returns a correct arm built from the status alone.
 *
 * There is deliberately no overload that returns `Promise<AppError>` for a
 * `Response` argument. It type-checks, but every `catch` binds `unknown`, which
 * selects the synchronous signature and hands the caller a promise typed as an
 * `AppError` — a failure that surfaces far from its cause.
 *
 * @param value The caught value.
 * @param options Extra context the caller holds.
 */
export function toAppError(value: unknown, options: ToAppErrorOptions = {}): AppError {
  const url = options.url ?? null;

  if (value instanceof HttpErrorResponse) {
    return fromHttpErrorResponse(value);
  }

  // The thrown value's own identity decides first, and the signal only rescues
  // values that cannot identify themselves. Order matters:
  // `AbortSignal.timeout()` sets `aborted` *and* rejects with a TimeoutError, so
  // testing the signal first would classify every timeout as a cancellation —
  // silently, and only for the callers that supply the signal.
  //
  // The reason is read off the signal too, because `AbortSignal.any([stop,
  // timeout])` propagates the reason from whichever source fired.
  const reason = nameOf(value) ?? nameOf(options.signal?.reason);

  // A timeout is a failed request, not a cancelled one: it should read as a
  // network problem and stay retriable.
  if (reason === 'TimeoutError') {
    return networkAppError(url, value);
  }

  // `abort(reason)` rejects with the reason itself, which is often a plain
  // string. When the value names nothing, an aborted signal is the only evidence
  // that the user pressed Stop rather than the request failing.
  if (reason === 'AbortError' || (reason === null && options.signal?.aborted === true)) {
    return abortedAppError(url, value);
  }

  // `fetch` reports every transport failure — DNS, TLS, offline, and CORS
  // rejections alike — as a bare TypeError with no detail.
  if (value instanceof TypeError) {
    return networkAppError(url, value);
  }

  if (isResponseLike(value)) {
    return buildAppError({
      status: value.status,
      problem: null,
      url: url ?? (typeof value.url === 'string' && value.url !== '' ? value.url : null),
      cause: value,
    });
  }

  return clientAppError(value);
}

function fromHttpErrorResponse(response: HttpErrorResponse): AppError {
  const url = response.url ?? null;

  // status 0 is how HttpClient reports a request that never reached the server.
  if (response.status === 0) {
    return isAbortError(response.error)
      ? abortedAppError(url, response)
      : networkAppError(url, response);
  }

  return buildAppError({
    status: response.status,
    // `error` is already parsed when the body was JSON. When it was not, HttpClient
    // supplies `{ error: SyntaxError, text: string }`, which parseProblemDetails
    // rejects — so this degrades to the plain `http` arm instead of throwing.
    problem: parseProblemDetails(response.error),
    url,
    cause: response,
    fallbackTitle: response.statusText || undefined,
  });
}

function nameOf(value: unknown): string | null {
  if (typeof value !== 'object' || value === null || !('name' in value)) {
    return null;
  }

  const name = (value as { name?: unknown }).name;

  return typeof name === 'string' ? name : null;
}

function isAbortError(value: unknown): boolean {
  return nameOf(value) === 'AbortError';
}

function isResponseLike(value: unknown): value is { status: number; url?: string } {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as { status?: unknown }).status === 'number' &&
    typeof (value as { text?: unknown }).text === 'function'
  );
}
