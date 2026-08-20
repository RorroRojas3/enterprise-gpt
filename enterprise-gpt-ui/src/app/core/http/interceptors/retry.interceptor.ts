import {
  HttpContext,
  HttpContextToken,
  HttpErrorResponse,
  HttpInterceptorFn,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { retry, throwError, timer } from 'rxjs';
import { RETRY_POLICY, computeBackoffDelay, isTransientRetriable } from '../../errors/retry-policy';
import { toAppError } from '../../errors/to-app-error';

/**
 * Opts one request out of the transient-failure retry.
 *
 * For requests whose error body this interceptor cannot classify — anything not
 * `responseType: 'json'`, where `HttpClient` hands back a `Blob` that
 * `parseProblemDetails` rejects. Such a failure degrades to the plain `http` arm, so
 * an application-typed 503 that must never be retried is indistinguishable from a
 * gateway 503 that should be. Retrying blind is three round trips that cannot succeed
 * and seconds of delay in front of an actionable message.
 */
const SKIP_RETRY = new HttpContextToken<boolean>(() => false);

/**
 * Builds an {@link HttpContext} that opts out of the retry.
 *
 * @param context An existing context to extend, when the caller has one.
 */
export function skipRetry(context = new HttpContext()): HttpContext {
  return context.set(SKIP_RETRY, true);
}

/**
 * Retries transient failures on idempotent requests with jittered, capped backoff.
 *
 * Restricted to `GET`: no other verb is safe to replay, and the chat stream is a
 * `POST` issued over raw `fetch`, which never reaches an `HttpClient` interceptor
 * at all. A `GET` can additionally opt out with {@link skipRetry}.
 *
 * Anything not retried is rethrown as the identical {@link HttpErrorResponse}
 * instance, so downstream handlers see the original object.
 *
 * **Keep this first in `withInterceptors`.** When the authentication interceptor
 * lands it must run inside each retry, so a retried request acquires a fresh
 * token rather than replaying the one that already failed.
 */
export const retryInterceptor: HttpInterceptorFn = (request, next) => {
  const policy = inject(RETRY_POLICY);

  if (request.method !== 'GET' || policy.maxRetries <= 0 || request.context.get(SKIP_RETRY)) {
    return next(request);
  }

  return next(request).pipe(
    retry({
      count: policy.maxRetries,
      delay: (error: unknown, retryCount: number) => {
        if (!(error instanceof HttpErrorResponse) || !isTransientRetriable(toAppError(error))) {
          return throwError(() => error);
        }

        return timer(computeBackoffDelay(retryCount - 1, policy));
      },
    }),
  );
};
