/** The four toast types the design defines, each with its own accent and icon. */
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
 * How long each tone survives.
 *
 * `satisfies` rather than an annotation, mirroring `NOTIFIABLE_KINDS` in
 * `core/errors/error-message.ts`: a new tone cannot be added without deciding whether
 * it disappears on its own. That makes the design's rule — "an error that disappears
 * before it is read is a defect" — a property of the type system rather than a
 * convention someone has to remember.
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
