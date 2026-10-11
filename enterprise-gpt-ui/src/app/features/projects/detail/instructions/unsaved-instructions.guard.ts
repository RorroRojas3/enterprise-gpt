import { CanDeactivateFn } from '@angular/router';

/** What the guard needs from the panel it protects. */
export interface ConfirmsDiscard {
  /** True when nothing is unsaved or the user explicitly discarded. */
  confirmDiscard(): Promise<boolean>;
}

/**
 * No `beforeunload`: only in-app navigation lets a modal name what is lost. Returning `false` is
 * safe here, unlike in the auth guards, because it cancels back onto a rendered panel.
 */
export const unsavedInstructionsGuard: CanDeactivateFn<ConfirmsDiscard> = (component) =>
  component.confirmDiscard();
