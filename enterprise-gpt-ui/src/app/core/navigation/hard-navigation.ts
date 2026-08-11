import { DOCUMENT, Injectable, inject } from '@angular/core';

/**
 * Replaces the current document, as distinct from routing within it.
 *
 * The two are not interchangeable and the difference matters most during sign-out: a
 * router navigation re-runs the guards, keeps the same MSAL instance, and cannot
 * release the `interaction_in_progress` lock MSAL takes before it redirects. Only a
 * fresh document does that. Wherever this service is used, the comment at the call site
 * says which of those properties it is relying on.
 *
 * It exists as a service rather than a bare `location.assign` because `Location` is not
 * patchable in jsdom, so a direct call is a line no unit test can reach — and the
 * sign-out fallback path is exactly the one that most needs proving.
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
