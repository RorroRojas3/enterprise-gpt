import { DestroyRef, Injectable, inject } from '@angular/core';

/** The one message this channel carries. */
const SIGNED_OUT = 'signed-out';

const CHANNEL_NAME = 'egpt.session';

/**
 * Tells the application's other tabs that the session has ended.
 *
 * MSAL is configured with `cacheLocation: sessionStorage`, which is deliberate — a
 * token must not outlive the tab on a shared machine — but it is also *per tab*. Each
 * tab therefore holds its own token cache and its own populated stores, and signing out
 * in one leaves the others fully signed in. US-205's criteria are written against a
 * single tab; its story ("the next person sees nothing of mine") is not, and a second
 * window left open is the ordinary way that story fails in an office.
 *
 * `BroadcastChannel` rather than a `storage` event, because the `storage` event only
 * fires for `localStorage` — which is precisely the store US-205 requires sign-out to
 * leave alone.
 *
 * Receiving is one-way and terminal: a tab that is told the session ended tears down
 * and leaves for the signed-out page. It never broadcasts in response, so there is no
 * echo to guard against.
 */
@Injectable({ providedIn: 'root' })
export class SessionChannel {
  private readonly _channel = openChannel();

  constructor() {
    inject(DestroyRef).onDestroy(() => this._channel?.close());
  }

  /** Announces that this tab has signed out. Silently does nothing where unsupported. */
  post(): void {
    try {
      this._channel?.postMessage(SIGNED_OUT);
    } catch {
      // A closed or unavailable channel must not take down the sign-out that is
      // already under way in this tab.
    }
  }

  /**
   * Registers the teardown this tab runs when another one signs out.
   *
   * @param onSignedOut Invoked once per broadcast, in this tab.
   */
  listen(onSignedOut: () => void): void {
    this._channel?.addEventListener('message', (event: MessageEvent<unknown>) => {
      if (event.data === SIGNED_OUT) {
        onSignedOut();
      }
    });
  }
}

/**
 * `BroadcastChannel` is unavailable in a few hardened enterprise configurations, and
 * constructing it throws rather than returning undefined. Single-tab sign-out still
 * works everywhere; only the cross-tab courtesy is lost.
 */
function openChannel(): BroadcastChannel | null {
  try {
    return new BroadcastChannel(CHANNEL_NAME);
  } catch {
    return null;
  }
}
