import { Injectable, inject } from '@angular/core';
import {
  AccountInfo,
  BrowserAuthError,
  BrowserAuthErrorCodes,
  InteractionRequiredAuthError,
} from '@azure/msal-browser';
import { APP_CONFIG } from '../config/app-config.token';
import { MSAL_INSTANCE } from './msal-instance';

/** Raised when a token cannot be produced without leaving the page. */
export class InteractiveSignInRequiredError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = 'InteractiveSignInRequiredError';
  }
}

/**
 * The one place an API access token comes from.
 *
 * Both transports must share it. `HttpClient` goes through `authInterceptor`, and
 * EP-4's chat stream is a raw `fetch` that never reaches an `HttpClient`
 * interceptor at all — which is exactly why `MsalInterceptor` is not used. Two
 * token sources would mean two caches, two refresh schedules, and a stream that
 * outlives the token the rest of the app is using.
 *
 * A `Promise` rather than an `Observable` for the same reason: the stream path is
 * promise-shaped, and an interceptor converts a promise trivially.
 */
@Injectable({ providedIn: 'root' })
export class TokenService {
  private readonly _instance = inject(MSAL_INSTANCE);
  private readonly _scopes = [...inject(APP_CONFIG).auth.apiScopes];

  /**
   * Shared in-flight acquisition. A turn that fires a stream and two list requests
   * in the same tick would otherwise start three silent acquisitions, each racing
   * to write the same cache entry.
   *
   * A `forceRefresh` deliberately does not join it: that call exists precisely
   * because the cached token was rejected, so sharing a non-refreshing acquisition
   * would hand back the token that just failed. The guarantee stops at this class,
   * though — MSAL's own `acquireTokenSilent` de-duplication keys on a request
   * thumbprint that does **not** include `forceRefresh`, so a forced call overlapping
   * a plain one for the same account and scopes can still be merged inside MSAL. The
   * window is narrow, because this memo already leaves at most one MSAL call
   * outstanding at a time.
   */
  private _inFlight: Promise<string> | null = null;

  /** The account tokens are acquired for, or null when nobody is signed in. */
  get activeAccount(): AccountInfo | null {
    return this._instance.getActiveAccount() ?? this._instance.getAllAccounts()[0] ?? null;
  }

  /**
   * Acquires a bearer access token for the Enterprise GPT API.
   *
   * @param options `forceRefresh` bypasses the token cache; used once by
   *   `authInterceptor` after the API rejects a token with a 401.
   * @throws {InteractiveSignInRequiredError} When no account is signed in, or when
   *   Entra requires interaction. In the latter case a redirect has already been
   *   started and the page is on its way out, so the throw exists to stop the
   *   caller rather than to be recovered from.
   */
  async getToken(options?: { forceRefresh?: boolean }): Promise<string> {
    if (options?.forceRefresh) {
      return this.acquire(true);
    }

    this._inFlight ??= this.acquire(false).finally(() => {
      this._inFlight = null;
    });

    return this._inFlight;
  }

  private async acquire(forceRefresh: boolean): Promise<string> {
    const account = this.activeAccount;
    if (!account) {
      throw new InteractiveSignInRequiredError('No signed-in account.');
    }

    try {
      const result = await this._instance.acquireTokenSilent({
        scopes: this._scopes,
        account,
        forceRefresh,
      });

      return result.accessToken;
    } catch (cause) {
      if (cause instanceof InteractionRequiredAuthError) {
        // The only recovery is a full-page redirect: a consent, MFA or expired-refresh
        // requirement cannot be satisfied silently, and retrying would spin.
        await this.redirectForInteraction(account);
        throw new InteractiveSignInRequiredError('Interactive sign-in is required.', { cause });
      }

      throw cause;
    }
  }

  private async redirectForInteraction(account: AccountInfo): Promise<void> {
    try {
      await this._instance.acquireTokenRedirect({ scopes: this._scopes, account });
    } catch (cause) {
      // A second redirect while one is already committed is how redirect loops start.
      // The browser is already on its way out, so there is nothing to recover.
      if (
        cause instanceof BrowserAuthError &&
        cause.errorCode === BrowserAuthErrorCodes.interactionInProgress
      ) {
        return;
      }

      throw cause;
    }
  }
}
