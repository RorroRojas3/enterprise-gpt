import { HttpErrorResponse } from '@angular/common/http';
import { AppError } from './app-error';
import { abortedAppError, buildAppError, clientAppError, networkAppError } from './build-app-error';
import { parseProblemDetails } from './problem-details';

export interface ToAppErrorOptions {
  /** `abort(reason)` rejects with the bare reason, so the signal is what identifies a Stop. */
  readonly signal?: AbortSignal;
  /** The request URL, when the thrown value does not carry one. */
  readonly url?: string;
}

/**
 * Total and synchronous; a `Response` whose body must be read uses `toAppErrorFromResponse`.
 * No `Promise` overload: a `catch` binds `unknown`, which would pick it and mistype the result.
 */
export function toAppError(value: unknown, options: ToAppErrorOptions = {}): AppError {
  const url = options.url ?? null;

  if (value instanceof HttpErrorResponse) {
    return fromHttpErrorResponse(value);
  }

  // The value's own name wins: `AbortSignal.timeout()` also sets `aborted`. The signal's reason
  // covers `AbortSignal.any([stop, timeout])`, which forwards whichever fired.
  const reason = nameOf(value) ?? nameOf(options.signal?.reason);

  // A timeout is a failed request, not a cancelled one, so it stays retriable.
  if (reason === 'TimeoutError') {
    return networkAppError(url, value);
  }

  // When the value names nothing (often a string reason), an aborted signal is the only
  // evidence the user pressed Stop.
  if (reason === 'AbortError' || (reason === null && options.signal?.aborted === true)) {
    return abortedAppError(url, value);
  }

  // `fetch` reports every transport failure (DNS, TLS, offline, CORS) as a bare TypeError.
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
    // A non-JSON body arrives as `{ error, text }`, which degrades to the plain `http` arm.
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
