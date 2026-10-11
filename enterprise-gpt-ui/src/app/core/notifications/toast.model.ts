export type ToastTone = 'success' | 'error' | 'warning' | 'info';

export interface Toast {
  readonly id: string;
  readonly tone: ToastTone;
  /** The headline. For an error this is `userMessage(error)`. */
  readonly title: string;
  /** An optional second line — what to do about it, or what was affected. */
  readonly detail: string | null;
  /** Pre-formatted by `traceLine()`; rendered in the mono face. */
  readonly traceLine: string | null;
  /** Renders a retry affordance when set. */
  readonly retryLabel: string | null;
  /** Milliseconds until self-dismissal. Zero means it stays until dismissed. */
  readonly dismissAfterMs: number;
}

/**
 * `satisfies` forces every new tone to decide whether it self-dismisses; an error that disappears
 * before it is read is a defect.
 */
export const AUTO_DISMISS_MS = {
  success: 5000,
  info: 5000,
  warning: 0,
  error: 0,
} as const satisfies Record<ToastTone, number>;

/** The sprite glyph each tone renders. */
export const TOAST_ICONS = {
  success: 'bi-check-circle-fill',
  error: 'bi-x-circle-fill',
  warning: 'bi-exclamation-triangle-fill',
  info: 'bi-info-circle',
} as const satisfies Record<ToastTone, string>;
