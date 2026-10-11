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
 * Goes after `retryInterceptor`, so a replay acquires a fresh token. A 401 retries once with a
 * forced refresh; `catchError` does not catch its own replay, so no attempt counter is needed.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const apiUrl = inject(ApiUrl);
  const tokens = inject(TokenService);

  // Only our API gets the bearer token, never config.json, the sprite or a third-party host.
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

/** `defer`, so each retry re-subscription acquires a token rather than reusing the rejected one. */
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
