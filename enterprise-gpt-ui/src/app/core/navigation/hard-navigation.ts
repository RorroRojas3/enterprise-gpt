import { DOCUMENT, Injectable, inject } from '@angular/core';

/**
 * Replaces the document. Unlike routing, it skips the guards, drops the MSAL instance and
 * releases its `interaction_in_progress` lock. A service because jsdom cannot patch `Location`.
 */
@Injectable({ providedIn: 'root' })
export class HardNavigation {
  private readonly _window = inject(DOCUMENT).defaultView;

  /** Leaves for `url`. Nothing after this call is guaranteed to run. */
  assign(url: string): void {
    this._window?.location.assign(url);
  }

  /** Reloads the current document, discarding every in-memory instance with it. */
  reload(): void {
    this._window?.location.reload();
  }
}
