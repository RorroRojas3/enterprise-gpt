import { Injectable, inject } from '@angular/core';
import {
  AccountInfo,
  BrowserAuthError,
  BrowserAuthErrorCodes,
  InteractionRequiredAuthError,
} from '@azure/msal-browser';
import { APP_CONFIG } from '@core/config/app-config-token';
import { MSAL_INSTANCE } from './msal-instance-token';

/** Raised when a token cannot be produced without leaving the page. */
export class InteractiveSignInRequiredError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = 'InteractiveSignInRequiredError';
  }
}

/**
 * The one token source for `authInterceptor` and the chat stream's raw `fetch`, so both share a
 * cache and refresh schedule. Promise-shaped because the stream path is.
 */
@Injectable({ providedIn: 'root' })
export class AccessTokenClient {
  private readonly _instance = inject(MSAL_INSTANCE);
  private readonly _scopes = [...inject(APP_CONFIG).auth.apiScopes];

  /**
   * Shared so concurrent callers start one silent acquisition. `forceRefresh` skips it, since
   * joining would return the rejected token; MSAL may still merge the two, as its request
   * thumbprint ignores `forceRefresh`.
   */
  private _inFlight: Promise<string> | null = null;

  get activeAccount(): AccountInfo | null {
    return this._instance.getActiveAccount() ?? this._instance.getAllAccounts()[0] ?? null;
  }

  /**
   * @param options `forceRefresh` bypasses the cache; `authInterceptor` uses it once after a 401.
   * @throws {InteractiveSignInRequiredError} With no account, or once an interaction redirect has
   *   started; it stops the caller and is not meant to be recovered from.
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
      // A redirect is already committed and the page is leaving, so there is nothing to recover.
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
