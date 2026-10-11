/**
 * The only `localStorage` call site (`scripts/check-forbidden-apis.mjs` enforces it). It survives
 * sign-out, so it may hold only values about the browser, never about the user.
 */
export const PREFERENCE_KEYS = {
  /**
   * Duplicated in the pre-paint script in `index.html`, which runs before any module exists;
   * `scripts/check-tokens.mjs` fails the build if the two drift.
   */
  theme: 'egpt.theme',
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
