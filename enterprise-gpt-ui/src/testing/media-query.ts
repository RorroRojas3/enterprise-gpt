/**
 * A controllable `matchMedia` for tests.
 *
 * jsdom implements no `matchMedia` at all, so anything that reads a media query —
 * `ThemeService`, and the viewport-aware components from EP-3 onwards — throws
 * without this. `setup-dom.ts` installs it before every test file; individual specs
 * drive it with {@link setMediaQuery}.
 */

/** The query `ThemeService` watches. */
export const PREFERS_DARK = '(prefers-color-scheme: dark)';

/** The query components use for the mobile breakpoint. */
export const NARROW_VIEWPORT = '(max-width: 767px)';

interface Registry {
  readonly matches: Map<string, boolean>;
  readonly listeners: Map<string, Set<(event: MediaQueryListEvent) => void>>;
}

const REGISTRY_KEY = '__egptMediaQueryRegistry';

/**
 * Held on `globalThis` rather than in module scope on purpose. The setup file and the
 * spec are separate build entries, so they can end up with distinct instances of this
 * module — and then the stub a spec drives would not be the stub the code under test
 * reads. One registry keyed on the global removes that failure mode entirely.
 */
function registry(): Registry {
  const global = globalThis as Record<string, unknown>;
  global[REGISTRY_KEY] ??= { matches: new Map(), listeners: new Map() } satisfies Registry;
  return global[REGISTRY_KEY] as Registry;
}

// Deliberately not `implements Partial<MediaQueryList>`: the real interface's
// addEventListener is the full overloaded DOM signature, and satisfying it would mean
// widening the listener type to EventListenerOrEventListenerObject for no test value.
// The single cast in installMatchMedia() is the honest place for the gap.
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
