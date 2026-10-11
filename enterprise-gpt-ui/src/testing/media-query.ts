/**
 * A controllable `matchMedia`, which jsdom lacks. `setup-dom.ts` installs it before every test
 * file; specs drive it with {@link setMediaQuery}.
 */

// Relative, not `@shared/*`: `setup-dom.ts` loads this as a Vite setupFile, outside the alias map.
import { MOBILE_VIEWPORT, TABLET_VIEWPORT } from '../app/shared/layout/breakpoints';

/** The query `ThemeService` watches. */
export const PREFERS_DARK = '(prefers-color-scheme: dark)';

/** Re-exported, not restated: the fake matches query strings, so a typo would drive nothing. */
export const NARROW_VIEWPORT = MOBILE_VIEWPORT;

/** The tablet band, re-exported likewise. Mutually exclusive with {@link NARROW_VIEWPORT}. */
export const TABLET_VIEWPORT_QUERY = TABLET_VIEWPORT;

interface Registry {
  readonly matches: Map<string, boolean>;
  readonly listeners: Map<string, Set<(event: MediaQueryListEvent) => void>>;
}

const REGISTRY_KEY = '__egptMediaQueryRegistry';

/** On `globalThis`: the setup file and a spec can load distinct copies of this module. */
function registry(): Registry {
  const global = globalThis as Record<string, unknown>;
  global[REGISTRY_KEY] ??= { matches: new Map(), listeners: new Map() } satisfies Registry;
  return global[REGISTRY_KEY] as Registry;
}

// Not `implements Partial<MediaQueryList>`: matching the overloaded addEventListener buys
// nothing, and the one cast in installMatchMedia() covers the gap.
class FakeMediaQueryList {
  onchange: ((this: MediaQueryList, event: MediaQueryListEvent) => unknown) | null = null;

  constructor(readonly media: string) {}

  get matches(): boolean {
    return registry().matches.get(this.media) ?? false;
  }

  addEventListener(_type: 'change', listener: (event: MediaQueryListEvent) => void): void {
    const { listeners } = registry();
    const forQuery = listeners.get(this.media) ?? new Set();
    forQuery.add(listener);
    listeners.set(this.media, forQuery);
  }

  removeEventListener(_type: 'change', listener: (event: MediaQueryListEvent) => void): void {
    registry().listeners.get(this.media)?.delete(listener);
  }

  /** Pre-2020 API. Kept because library code still feature-detects it. */
  addListener(listener: (event: MediaQueryListEvent) => void): void {
    this.addEventListener('change', listener);
  }

  removeListener(listener: (event: MediaQueryListEvent) => void): void {
    this.removeEventListener('change', listener);
  }

  dispatchEvent(): boolean {
    return true;
  }
}

const INSTALLED = '__egptFakeMatchMedia';

/** Idempotent, so a spec may call it directly without fighting the global setup. */
export function installMatchMedia(): void {
  if (typeof window === 'undefined') {
    return;
  }

  const existing = window.matchMedia as unknown as Record<string, unknown> | undefined;
  if (existing?.[INSTALLED] === true) {
    return;
  }

  const fake = (query: string) => new FakeMediaQueryList(query) as unknown as MediaQueryList;
  (fake as unknown as Record<string, unknown>)[INSTALLED] = true;
  window.matchMedia = fake as typeof window.matchMedia;
}

/** Sets a query's value and notifies every listener, as a real change would. */
export function setMediaQuery(query: string, isMatching: boolean): void {
  const { matches, listeners } = registry();
  matches.set(query, isMatching);

  const event = { matches: isMatching, media: query } as MediaQueryListEvent;
  for (const listener of listeners.get(query) ?? []) {
    listener(event);
  }
}

/** Clears every registered value and listener. Call between tests. */
export function resetMediaQueries(): void {
  const { matches, listeners } = registry();
  matches.clear();
  listeners.clear();
}
