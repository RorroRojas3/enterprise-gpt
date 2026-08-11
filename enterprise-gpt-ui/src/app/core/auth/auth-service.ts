import { Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AccountInfo } from '@azure/msal-browser';
import { Dispatcher } from '@ngrx/signals/events';
import { AppError } from '../errors/app-error';
import { toAppError } from '../errors/to-app-error';
import { APP_CONFIG } from '../config/app-config.token';
import { hideStartupShell } from '../config/fatal-shell';
import { injectSignedOut, sessionEvents } from '../events/session-events';
import { HardNavigation } from '../navigation/hard-navigation';
import { SessionChannel } from '../session/session-channel';
import { CHAT_ROUTE, SIGNED_OUT_ROUTE } from './auth-routes';
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
  private readonly _dispatcher = inject(Dispatcher);
  private readonly _channel = inject(SessionChannel);
  private readonly _navigation = inject(HardNavigation);
  private _handshake: Promise<AuthHandshake> | null = null;

  private readonly _signingOut = signal(false);

  /**
   * Latched from the moment sign-out begins until the document is replaced.
   *
   * Read by `App`, which swaps the shell for an interstitial, and by all three guards,
   * which refuse to activate. Both are needed: the first removes every link a user
   * could press, the second stops code that navigates anyway.
   */
  readonly isSigningOut = this._signingOut.asReadonly();

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
    //
    // It is also the recovery path for a stuck interaction lock: MSAL leaves its
    // `interaction_in_progress` flag set at `SIGNOUT` when `logoutRedirect` throws
    // after taking it, and clearing this memo is what lets the next `ready()` re-run
    // `handleRedirectPromise`, which is the only thing that releases it.
    //
    // `signOut` dispatches the event this subscribes to, so this is a deliberate
    // self-loop. It is safe only because the effect is idempotent and non-reentrant:
    // three assignments, no I/O, nothing that could call back into `signOut`. Adding
    // anything else here breaks that.
    injectSignedOut()
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        this._handshake = null;
        // The inconclusive counter is per sign-in attempt, and sign-out ends the one
        // it was counting. A tenant broken badly enough to loop will simply start the
        // count again on the next attempt.
        removeSessionStorage(INCONCLUSIVE_KEY);
        removeSessionStorage(RETURN_URL_KEY);
      });

    // Another tab signed out. Tear this one down the same way and leave — a tab left
    // showing a populated shell is the shared-machine failure this exists to prevent.
    this._channel.listen(() => {
      if (this._signingOut()) {
        return;
      }

      this._signingOut.set(true);
      // The broadcast can land mid-navigation, and a guard latching off cancels it.
      // `provideStartupShellHandoff` does not treat NavigationCancel as its cue, and
      // the shell is a block in normal flow rather than an overlay — so without this
      // the "Starting Enterprise GPT…" card stays stacked above the interstitial for
      // as long as the wipe takes.
      hideStartupShell();
      this._dispatcher.dispatch(sessionEvents.signedOut(), { scope: 'global' });

      void this._instance
        .clearCache()
        .catch(() => undefined)
        // A fresh document, so this tab's MSAL instance and its in-memory state go
        // with it rather than being cleaned up piecemeal.
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
   * Ends the session everywhere it exists, then leaves for Entra's end-session
   * endpoint.
   *
   * The invariant, and the reason for the ordering below: **once this begins, the
   * application never returns to an interactive signed-in state.** It either leaves for
   * Entra or replaces the document. Nothing in between is a state any screen renders.
   *
   * The event is dispatched *before* `logoutRedirect`, which looks premature and is
   * not. MSAL clears its own token cache before it does authority discovery, so every
   * failure that involves the network has already emptied the cache by the time it
   * throws — dispatching first is what keeps this application's state consistent with
   * MSAL's, rather than briefly ahead of it. The only failures that throw with the
   * cache intact are synchronous preflight ones, and the `catch` below covers those by
   * wiping it outright.
   *
   * The latch, the broadcast and the dispatch all land in the same tick. `App` is
   * `OnPush` under zoneless change detection, so the interstitial and the emptied
   * stores settle into one render: no screen ever paints a signed-in shell with a
   * cleared `SessionStore` behind it.
   *
   * `postLogoutRedirectUri` is deliberately not passed in the request. `buildMsalConfig`
   * already resolves it absolutely against `document.baseURI`, and a second source for
   * the same value is a drift waiting to happen.
   */
  async signOut(): Promise<void> {
    if (this._signingOut()) {
      return;
    }

    // Captured before anything can clear it: MSAL empties its cache during logout, and
    // an account read afterwards is null.
    const account = this.account;

    this._signingOut.set(true);
    this._channel.post();
    this._dispatcher.dispatch(sessionEvents.signedOut(), { scope: 'global' });

    try {
      await this._instance.logoutRedirect({ account });
    } catch {
      // The browser is still here, so the end-session round trip never happened — and
      // that means the Entra session cookie is still live. Landing on
      // `postLogoutRedirectUri` would therefore send the user through `/chat` and
      // `authGuard` straight back into a silent re-authentication, which is exactly the
      // shared-machine failure this story is about. A local wipe and an explicit
      // signed-out page is the honest outcome instead.
      //
      // `clearCache()` with **no argument** on purpose: passing an account removes only
      // that account, while the bare call clears browser storage and the crypto
      // keystore. When Entra cannot be reached, the full local wipe is the whole point.
      try {
        await this._instance.clearCache();
      } catch {
        // Best effort. A failed wipe must not stop the user from leaving the page.
      }

      // A hard navigation, never the router: a fresh document is the only thing that
      // releases MSAL's `interaction_in_progress` lock, which `logoutRedirect` takes
      // before it can fail. A router navigation would also re-run the guards this
      // sign-out has latched off.
      this._navigation.assign(SIGNED_OUT_ROUTE);
    }
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
