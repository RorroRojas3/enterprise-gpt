import { DestroyRef, Injectable, inject } from '@angular/core';

const SIGNED_OUT = 'signed-out';

const CHANNEL_NAME = 'egpt.session';

/**
 * Tells other tabs the session ended: MSAL's `sessionStorage` cache is per tab, so signing out
 * in one leaves the rest signed in. Not a `storage` event, which only fires for `localStorage`.
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
      // A closed channel must not abort this tab's own sign-out.
    }
  }

  /** Registers the teardown this tab runs when another tab signs out. */
  listen(onSignedOut: () => void): void {
    this._channel?.addEventListener('message', (event: MessageEvent<unknown>) => {
      if (event.data === SIGNED_OUT) {
        onSignedOut();
      }
    });
  }
}

/** Some hardened enterprise browsers lack `BroadcastChannel`, and constructing it throws. */
function openChannel(): BroadcastChannel | null {
  try {
    return new BroadcastChannel(CHANNEL_NAME);
  } catch {
    return null;
  }
}
