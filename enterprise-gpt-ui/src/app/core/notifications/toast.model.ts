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
  /** Renders the toast's one button when set. */
  readonly actionLabel: string | null;
  readonly key: string | null;
}

/** Every toast dismisses itself after this long, whatever its tone. */
export const AUTO_DISMISS_MS = 3000;

/** The sprite glyph each tone renders. */
export const TOAST_ICONS = {
  success: 'bi-check-circle-fill',
  error: 'bi-x-circle-fill',
  warning: 'bi-exclamation-triangle-fill',
  info: 'bi-info-circle',
} as const satisfies Record<ToastTone, string>;
