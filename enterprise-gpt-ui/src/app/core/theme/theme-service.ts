import { DOCUMENT, DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { PREFERENCE_KEYS, readPreference, writePreference } from '@core/storage/local-preferences';

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
 * Owns `data-bs-theme` and `color-scheme` on `<html>` for the lifetime of the app.
 *
 * The pre-paint script in `index.html` sets both before this class exists — that is
 * what prevents a light frame — and this service takes over from the same stored
 * value, so the handover changes nothing on screen.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly systemDark = signal(false);
  private readonly preferenceValue = signal<ThemePreference>('system');

  /**
   * The user's choice, including the tri-state `system`.
   *
   * Read-only outside this class on purpose. `apply()` is imperative rather than an
   * effect (see its comment), so a writable signal here would let a caller flip
   * `theme()` — and every consumer reading it — while `<html>` kept the old value and
   * nothing was persisted. Writes go through {@link set} and {@link toggle}.
   */
  readonly preference = this.preferenceValue.asReadonly();

  /**
   * The theme in effect. Separate from {@link preference} because every consumer
   * wants the resolved value; folding both into one signal would make each caller
   * re-derive this.
   */
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

  /**
   * Deliberately not an `effect()`. This is a DOM write outside Angular's render
   * tree; an effect would defer it to the next flush and entangle it with the
   * exhaustive check-no-changes pass for no benefit. Writing synchronously is what
   * puts the flip in the same frame as the gesture that caused it.
   */
  private apply(): void {
    const root = this.document.documentElement;
    const resolved = this.theme();
    root.setAttribute('data-bs-theme', resolved);
    root.style.colorScheme = resolved;
  }
}
