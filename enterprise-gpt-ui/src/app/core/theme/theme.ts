import { DOCUMENT, DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import {
  PREFERENCE_KEYS,
  readPreference,
  writePreference,
} from '@shared/utils/storage/local-preferences';

/** What the user chose. `system` defers to `prefers-color-scheme`. */
export type ThemePreference = 'light' | 'dark' | 'system';

/** What is actually on `<html>`. */
export type ResolvedTheme = 'light' | 'dark';

const SYSTEM_DARK_QUERY = '(prefers-color-scheme: dark)';

function readStoredPreference(): ThemePreference {
  // Following the OS is the right answer when we cannot know what the user picked,
  // which is what a null from blocked storage means.
  const stored = readPreference(PREFERENCE_KEYS.theme);

  return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
}

/**
 * Owns `data-bs-theme` and `color-scheme` on `<html>`, taking over from the pre-paint script in
 * `index.html` with the same stored value so the handover changes nothing on screen.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly systemDark = signal(false);
  private readonly preferenceValue = signal<ThemePreference>('system');

  /** Read-only so every write goes through {@link set}, which also applies and persists it. */
  readonly preference = this.preferenceValue.asReadonly();

  /** The theme in effect, with `system` resolved. */
  readonly theme = computed<ResolvedTheme>(() => {
    const preference = this.preference();
    return preference === 'system' ? (this.systemDark() ? 'dark' : 'light') : preference;
  });

  constructor() {
    this.preferenceValue.set(readStoredPreference());

    const query = this.document.defaultView?.matchMedia?.(SYSTEM_DARK_QUERY);
    if (query) {
      this.systemDark.set(query.matches);
      const onChange = (event: MediaQueryListEvent): void => {
        this.systemDark.set(event.matches);
        this.apply();
      };
      query.addEventListener('change', onChange);
      this.destroyRef.onDestroy(() => query.removeEventListener('change', onChange));
    }

    this.apply();
  }

  /** Records a preference and applies it. `system` resumes following the OS. */
  set(preference: ThemePreference): void {
    this.preferenceValue.set(preference);
    writePreference(PREFERENCE_KEYS.theme, preference);
    this.apply();
  }

  /** Flips to the opposite of what is currently on screen, pinning the choice. */
  toggle(): void {
    this.set(this.theme() === 'dark' ? 'light' : 'dark');
  }

  /** Not an `effect()`: writing synchronously puts the flip in the same frame as the gesture. */
  private apply(): void {
    const root = this.document.documentElement;
    const resolved = this.theme();
    root.setAttribute('data-bs-theme', resolved);
    root.style.colorScheme = resolved;
  }
}
