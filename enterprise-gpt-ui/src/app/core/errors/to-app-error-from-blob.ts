import { HttpErrorResponse } from '@angular/common/http';
import { AppError } from './app-error';
import { toAppError } from './to-app-error';
import { toAppErrorFromResponse } from './to-app-error-from-response';

/**
 * Normalizes a failure from a request that did not ask for JSON.
 *
 * `HttpClient` parses an error body only when `responseType` is `json`; on a `blob`
 * request it hands the body back as a `Blob`, which `parseProblemDetails` rejects. Left
 * alone, every application problem type on such a request degrades to the plain `http`
 * arm — so a 503 that means "this deployment cannot render that format" becomes
 * indistinguishable from a 503 that means "try again", which is the one distinction the
 * typed problem exists to draw.
 *
 * Reading the body is asynchronous, which is why this is separate from
 * {@link toAppError} rather than folded into it: that function is deliberately total and
 * synchronous, and an overload returning a promise would be selected by a `catch`
 * binding `unknown` and hand the caller a promise typed as an `AppError`.
 *
 * @param cause The caught value.
 * @param url The request URL, for the error's context when the response carries none.
 */
export function toAppErrorFromBlobError(cause: unknown, url: string): Promise<AppError> {
  if (!(cause instanceof HttpErrorResponse) || !(cause.error instanceof Blob)) {
    return Promise.resolve(toAppError(cause, { url }));
  }

  const body = cause.error;

  return toAppErrorFromResponse({
    url: cause.url ?? url,
    status: cause.status,
    bodyUsed: false,
    headers: { get: (name: string) => cause.headers.get(name) },
    text: () => body.text(),
  });
}
