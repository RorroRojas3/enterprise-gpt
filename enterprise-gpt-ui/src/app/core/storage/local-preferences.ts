/**
 * The only place in the application that touches `localStorage`.
 *
 * US-205 requires that after sign-out the browser retains the user's theme and sidebar
 * preferences and **nothing else** — no conversation, project, document, or user data.
 * That invariant is impossible to review one call site at a time, so there is exactly
 * one call site, its keys are enumerated here, and `scripts/check-forbidden-apis.mjs`
 * fails the build on any `localStorage.setItem` outside this file.
 *
 * The rule that governs what may be added: `localStorage` survives sign-out by design,
 * so it holds only values that are **about the browser, not about the user** — how this
 * machine renders the app. Anything identifying, anything fetched from the API, and
 * anything a second person on a shared machine should not see belongs in
 * `sessionStorage` (where MSAL's token cache already lives) or in a store.
 */
export const PREFERENCE_KEYS = {
  /**
   * US-105. Duplicated in the pre-paint script in `index.html`, which runs before any
   * module exists; `scripts/check-tokens.mjs` fails the build if the two drift.
   */
  theme: 'egpt.theme',
  /** US-301's sidebar collapse state. Listed ahead of its story so the allowlist is complete. */
  sidebar: 'egpt.sidebar',
} as const;

/** A key this application is permitted to persist across sign-out. */
export type PreferenceKey = (typeof PREFERENCE_KEYS)[keyof typeof PREFERENCE_KEYS];

/** Every allowed key, for the spec that asserts nothing else survives sign-out. */
export const ALLOWED_PREFERENCE_KEYS: readonly string[] = Object.values(PREFERENCE_KEYS);

/** Reads a preference. Returns null when unset, or when storage is unavailable. */
export function readPreference(key: PreferenceKey): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    // Private mode, blocked storage, and enterprise policies throw on access rather
    // than returning null. Losing a preference is not a failure worth propagating.
    return null;
  }
}

/** Records a preference. Silently does nothing when storage is unavailable. */
export function writePreference(key: PreferenceKey, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // See readPreference. Blocked storage costs persistence across reloads, not the
    // change the user just made.
  }
}

/** Forgets a preference. */
export function clearPreference(key: PreferenceKey): void {
  try {
    localStorage.removeItem(key);
  } catch {
    // See readPreference.
  }
}
