import { Injectable, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AccountInfo } from '@azure/msal-browser';
import { AppError } from '../errors/app-error';
import { toAppError } from '../errors/to-app-error';
import { APP_CONFIG } from '../config/app-config.token';
import { injectSignedOut } from '../events/session-events';
import { CHAT_ROUTE } from './auth-routes';
import { MSAL_INSTANCE } from './msal-instance';

/** Where the sign-in redirect should return the user, across the trip to Entra. */
const RETURN_URL_KEY = 'egpt.returnUrl';

/** Counts handshakes that came back with neither an account nor an error. */
const INCONCLUSIVE_KEY = 'egpt.authAttempts';

/** How many inconclusive handshakes to absorb before showing the failure page. */
const MAX_INCONCLUSIVE = 1;

/** The outcome of resolving a sign-in redirect. */
export interface AuthHandshake {
  /** The signed-in account, or null when nobody is signed in. */
  readonly account: AccountInfo | null;
  /** Set when the redirect came back carrying a failure. */
  readonly error: AppError | null;
}

/**
 * Sign-in, sign-out, and the once-per-page-load redirect handshake.
 *
 * The handshake is memoized rather than repeated because it is the operation that
 * clears MSAL's `interaction_in_progress` lock. It therefore has to run on **every**
 * page load and not only on `/auth`: a user who reloads the tab mid-redirect leaves
 * that lock set, and every later sign-in attempt fails until the tab is closed. When
 * no interaction is outstanding it resolves in microseconds.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly _instance = inject(MSAL_INSTANCE);
  private readonly _scopes = [...inject(APP_CONFIG).auth.apiScopes];
  private _handshake: Promise<AuthHandshake> | null = null;

  /**
   * Whether the redirect response has already been applied.
   *
   * Deliberately **not** cleared on sign-out: the response was genuinely consumed by
   * this document, and MSAL keeps replaying it. See {@link handshake}.
   */
  private _redirectApplied = false;

  constructor() {
    // The memo holds an AccountInfo, so it is the same class of per-session handle the
    // stores clear on sign-out — and `withResetOnSignOut` reaches none of them. Left
    // alone, a guard consulting `ready()` after sign-out would keep activating for the
    // departed user until the page happened to reload.
    injectSignedOut()
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        this._handshake = null;
        removeSessionStorage(INCONCLUSIVE_KEY);
        removeSessionStorage(RETURN_URL_KEY);
      });
  }

  /** The active account, or null. Read on demand rather than mirrored into a signal. */
  get account(): AccountInfo | null {
    return this._instance.getActiveAccount() ?? this._instance.getAllAccounts()[0] ?? null;
  }

  /**
   * Resolves any sign-in redirect and selects the active account. Runs once per page
   * load however many callers ask.
   */
  ready(): Promise<AuthHandshake> {
    return (this._handshake ??= this.handshake());
  }

  /**
   * Starts an interactive sign-in. On the happy path the returned promise never
   * resolves, because the browser is leaving the page — but it very much can
   * **reject**, and a caller that does not handle that leaves the user on a dead
   * screen. MSAL performs authority discovery and takes its `interaction_in_progress`
   * lock *before* it navigates, so an unreachable Entra, a redirect attempted inside
   * a frame, and a lock left behind by an abandoned attempt all throw here.
   *
   * @param returnUrl The in-app URL to come back to once sign-in completes.
   */
  async signIn(returnUrl: string): Promise<void> {
    this.setReturnUrl(returnUrl);

    await this._instance.loginRedirect({ scopes: this._scopes });
  }

  /**
   * Reads and clears the URL stored by {@link signIn}.
   *
   * The value survives a round trip through the browser, so it is treated as
   * untrusted: only an in-app absolute path is honoured, and a protocol-relative or
   * absolute URL — an open redirect — falls back to the chat route.
   */
  consumeReturnUrl(): string {
    const stored = readSessionStorage(RETURN_URL_KEY);
    removeSessionStorage(RETURN_URL_KEY);

    return stored && stored.startsWith('/') && !stored.startsWith('//') ? stored : CHAT_ROUTE;
  }

  /**
   * Records a handshake that produced neither an account nor an error, and reports
   * whether that has now happened too often to keep trying.
   *
   * MSAL swallows some malformed responses and resolves `null` without throwing, so
   * "no account, no error" is a real outcome — and sending such a visitor onward to a
   * guarded route starts an unbounded `/auth` → `/chat` → Entra → `/auth` loop whose
   * only visible state is the startup shell. This counter is what turns that loop into
   * a terminal, explicable failure page.
   */
  noteInconclusiveHandshake(): boolean {
    const attempts = Number.parseInt(readSessionStorage(INCONCLUSIVE_KEY) ?? '0', 10);
    const next = Number.isFinite(attempts) ? attempts + 1 : 1;

    writeSessionStorage(INCONCLUSIVE_KEY, String(next));

    return next > MAX_INCONCLUSIVE;
  }

  private async handshake(): Promise<AuthHandshake> {
    try {
      // navigateToLoginRequestUrl: false is the load-bearing option here. Left at its
      // default of true, MSAL answers the redirect with `location.replace(...)` back
      // to the originating URL — a second full page load, a second config fetch, and
      // a second bootstrap. The return trip is this application's to make, through
      // the router, so the deep link is carried in session storage instead.
      const result = await this._instance.handleRedirectPromise({
        navigateToLoginRequestUrl: false,
      });

      // Applied once, then never again. MSAL memoizes `handleRedirectPromise` per hash
      // for the lifetime of the application instance, so every later call replays this
      // same response — account included. Re-applying it would re-select an account
      // that sign-out has since evicted, and reading the account *from* it would
      // report that user as still signed in. Both are the failure this memo is
      // cleared on sign-out to prevent, so the live instance is the only source read
      // after the first pass.
      if (!this._redirectApplied) {
        this._redirectApplied = true;

        if (result?.account) {
          this._instance.setActiveAccount(result.account);
        }
      }

      const account = this.account;
      if (account) {
        removeSessionStorage(INCONCLUSIVE_KEY);
      }

      return { account, error: null };
    } catch (cause) {
      return { account: null, error: toAppError(cause) };
    }
  }

  private setReturnUrl(returnUrl: string): void {
    writeSessionStorage(RETURN_URL_KEY, returnUrl);
  }
}

function readSessionStorage(key: string): string | null {
  try {
    return sessionStorage.getItem(key);
  } catch {
    // Private mode, blocked storage and enterprise policies throw on access rather
    // than returning null.
    return null;
  }
}

function writeSessionStorage(key: string, value: string): void {
  try {
    sessionStorage.setItem(key, value);
  } catch {
    // Losing the deep link or the attempt counter is a worse landing page, not a
    // failed sign-in, so it is not fatal.
  }
}

function removeSessionStorage(key: string): void {
  try {
    sessionStorage.removeItem(key);
  } catch {
    // See writeSessionStorage.
  }
}
