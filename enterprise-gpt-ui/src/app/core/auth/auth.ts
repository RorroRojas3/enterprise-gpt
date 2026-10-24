import { Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AccountInfo } from '@azure/msal-browser';
import { Dispatcher } from '@ngrx/signals/events';
import { APP_CONFIG } from '@core/config/app-config-token';
import { hideStartupShell } from '@core/config/fatal-shell';
import { AppError } from '@core/errors/app-error';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut, sessionEvents } from '@core/events/session-events';
import { HardNavigation } from '@core/navigation/hard-navigation';
import { CHAT_ROUTE, SIGNED_OUT_ROUTE } from '@core/navigation/route-paths';
import { SessionChannel } from '@core/session/session-channel';
import { MSAL_INSTANCE } from './msal-instance-token';

/** Where the sign-in redirect should return the user, across the trip to Entra. */
const RETURN_URL_KEY = 'egpt.returnUrl';

/** Counts handshakes that came back with neither an account nor an error. */
const INCONCLUSIVE_KEY = 'egpt.authAttempts';

/** How many inconclusive handshakes to absorb before showing the failure page. */
const MAX_INCONCLUSIVE = 1;

/** The outcome of resolving a sign-in redirect. */
export interface AuthHandshake {
  readonly account: AccountInfo | null;
  /** Set when the redirect came back carrying a failure. */
  readonly error: AppError | null;
}

/**
 * The handshake runs on every page load, not only on `/auth`: it is what clears an
 * `interaction_in_progress` lock left by a reload mid-redirect.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly _instance = inject(MSAL_INSTANCE);
  private readonly _scopes = [...inject(APP_CONFIG).auth.apiScopes];
  private readonly _dispatcher = inject(Dispatcher);
  private readonly _channel = inject(SessionChannel);
  private readonly _navigation = inject(HardNavigation);
  private _handshake: Promise<AuthHandshake> | null = null;

  private readonly _signingOut = signal(false);

  /**
   * Latched until the document is replaced. `App` swaps in an interstitial to remove every link,
   * and the guards refuse to activate to stop code that navigates anyway.
   */
  readonly isSigningOut = this._signingOut.asReadonly();

  /** Not cleared on sign-out: MSAL keeps replaying the consumed response. See {@link handshake}. */
  private _redirectApplied = false;

  constructor() {
    // The memo holds an AccountInfo, so a guard calling `ready()` would keep activating for the
    // departed user. Clearing it also lets the next `ready()` release a stuck interaction lock.
    // `signOut` dispatches this event, so the handler must stay idempotent and free of I/O.
    injectSignedOut()
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        this._handshake = null;
        // The inconclusive counter is per sign-in attempt, and sign-out ends the attempt.
        removeSessionStorage(INCONCLUSIVE_KEY);
        removeSessionStorage(RETURN_URL_KEY);
      });

    // Another tab signed out: a tab left showing a populated shell is the shared-machine failure.
    this._channel.listen(() => {
      if (this._signingOut()) {
        return;
      }

      this._signingOut.set(true);
      // A guard latching off cancels any in-flight navigation, which the shell handoff does not
      // treat as its cue, so the startup card would otherwise stay above the interstitial.
      hideStartupShell();
      this._dispatcher.dispatch(sessionEvents.signedOut(), { scope: 'global' });

      void this._instance
        .clearCache()
        .catch(() => undefined)
        // A fresh document discards this tab's MSAL instance and in-memory state wholesale.
        .finally(() => this._navigation.assign(SIGNED_OUT_ROUTE));
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
   * Never resolves on the happy path, but can reject: MSAL does authority discovery and takes its
   * lock before navigating, so an unreachable Entra, a framed page or a stale lock throw here.
   */
  async signIn(returnUrl: string): Promise<void> {
    this.setReturnUrl(returnUrl);

    await this._instance.loginRedirect({ scopes: this._scopes });
  }

  /**
   * Once this begins the app never returns to a signed-in state. Dispatching before
   * `logoutRedirect` is safe: MSAL clears its cache before any network failure can throw, and the
   * `catch` wipes it after the synchronous preflight ones.
   */
  async signOut(): Promise<void> {
    if (this._signingOut()) {
      return;
    }

    // Captured first: MSAL empties its cache during logout.
    const account = this.account;

    this._signingOut.set(true);
    this._channel.post();
    this._dispatcher.dispatch(sessionEvents.signedOut(), { scope: 'global' });

    try {
      await this._instance.logoutRedirect({ account });
    } catch {
      // The Entra session cookie is still live, so landing on `/chat` would silently
      // re-authenticate. Wipe locally instead: the bare `clearCache()` clears browser storage and
      // the crypto keystore, not just one account.
      try {
        await this._instance.clearCache();
      } catch {
        // Best effort. A failed wipe must not stop the user from leaving the page.
      }

      // Never the router: only a fresh document releases the interaction lock `logoutRedirect`
      // took, and the router would re-run the guards this sign-out latched off.
      this._navigation.assign(SIGNED_OUT_ROUTE);
    }
  }

  /**
   * Reads and clears the URL stored by {@link signIn}. Untrusted: anything but an in-app absolute
   * path (an open redirect) falls back to the chat route.
   */
  consumeReturnUrl(): string {
    const stored = readSessionStorage(RETURN_URL_KEY);
    removeSessionStorage(RETURN_URL_KEY);

    return stored && stored.startsWith('/') && !stored.startsWith('//') ? stored : CHAT_ROUTE;
  }

  /**
   * Counts a handshake with neither account nor error; true once there have been too many. MSAL
   * resolves some malformed responses as `null`, which would otherwise loop through Entra forever.
   */
  noteInconclusiveHandshake(): boolean {
    const attempts = Number.parseInt(readSessionStorage(INCONCLUSIVE_KEY) ?? '0', 10);
    const next = Number.isFinite(attempts) ? attempts + 1 : 1;

    writeSessionStorage(INCONCLUSIVE_KEY, String(next));

    return next > MAX_INCONCLUSIVE;
  }

  private async handshake(): Promise<AuthHandshake> {
    try {
      // Left true, MSAL `location.replace`s back to the origin URL: a second full bootstrap. The
      // router makes the return trip instead, with the deep link in session storage.
      const result = await this._instance.handleRedirectPromise({
        navigateToLoginRequestUrl: false,
      });

      // Applied once: MSAL replays this same response on every later call, and re-applying it
      // would re-select an account that sign-out has since evicted.
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
