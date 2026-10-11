import { HttpErrorResponse } from '@angular/common/http';
import { AppError } from './app-error';
import { toAppError } from './to-app-error';
import { toAppErrorFromResponse } from './to-app-error-from-response';

/**
 * For `blob` requests, whose error body `HttpClient` leaves as a `Blob`: unread, a typed 503 would
 * degrade to the retryable `http` arm. Async, so kept out of the synchronous {@link toAppError}.
 *
 * @param url The request URL, for when the response carries none.
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
