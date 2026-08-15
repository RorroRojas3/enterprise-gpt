import { CanDeactivateFn } from '@angular/router';

/** What the guard needs from the panel it protects. */
export interface ConfirmsDiscard {
  /**
   * Resolves true when it is safe to leave — either nothing is unsaved, or the user
   * explicitly discarded.
   */
  confirmDiscard(): Promise<boolean>;
}

/**
 * Stops an in-progress instructions edit from being lost to a navigation (US-904).
 *
 * `canDeactivate` rather than a `beforeunload` listener, and deliberately only that:
 * the criterion is about navigating away inside the app, which is where the edit is
 * genuinely recoverable and where a modal can name what is about to be lost. A browser
 * unload prompt is unstyled, unnameable and increasingly ignored by engines.
 *
 * Returning `false` is safe here, which it is not everywhere in this app: the
 * authentication guards must return a `UrlTree` because a cancelled navigation there
 * leaves the startup shell on screen with nothing behind it. This one cancels back onto
 * a fully rendered panel the reader is already looking at.
 */
export const unsavedInstructionsGuard: CanDeactivateFn<ConfirmsDiscard> = (component) =>
  component.confirmDiscard();
