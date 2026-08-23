import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable, catchError, defer, from, switchMap, throwError } from 'rxjs';
import { authErrorDecision } from '../../errors/retry-policy';
import { toAppError } from '../../errors/to-app-error';
import { ApiUrl } from '../../http/api-url';
import { TokenService } from '../token-service';

/**
 * Attaches the API bearer token to every `HttpClient` request bound for the API.
 *
 * **Order matters: this goes after `retryInterceptor`.** Retry has to sit outside
 * authentication so a replayed request acquires a token rather than replaying the
 * one that already failed — which is only true because of the `defer` below.
 *
 * The `ApiUrl.owns` check is not decoration. An interceptor that attached the token
 * to every outgoing request would hand the API's access token to whatever
 * third-party host a future feature calls, as well as to `config.json` and the
 * icon sprite.
 *
 * A 401 is retried exactly once with a forced refresh, and only when
 * {@link authErrorDecision} says so — the single source of truth for that decision.
 * The single retry needs no attempt counter: `catchError` does not catch the
 * observable its own handler returns, so the replay cannot re-enter this branch.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const apiUrl = inject(ApiUrl);
  const tokens = inject(TokenService);

  if (!apiUrl.owns(request.url)) {
    return next(request);
  }

  return authorize(request, next, tokens, false).pipe(
    catchError((error: unknown) => {
      if (
        !(error instanceof HttpErrorResponse) ||
        authErrorDecision(toAppError(error, { url: request.url })) !== 'refresh'
      ) {
        return throwError(() => error);
      }

      return authorize(request, next, tokens, true);
    }),
  );
};

/**
 * `defer` is load-bearing, not idiom. An interceptor function runs once per request
 * to build the chain, so a bare `from(tokens.getToken())` would capture one promise
 * and hand `retryInterceptor` the identical — already rejected — token on every
 * re-subscription, quietly turning the retry loop into three copies of one failure.
 */
function authorize(
  request: HttpRequest<unknown>,
  next: HttpHandlerFn,
  tokens: TokenService,
  forceRefresh: boolean,
): Observable<HttpEvent<unknown>> {
  return defer(() => from(tokens.getToken({ forceRefresh }))).pipe(
    switchMap((token) => next(request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }))),
  );
}
