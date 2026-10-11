/**
 * A controllable `IntersectionObserver`: jsdom has no layout, so specs set visibility with
 * {@link setIntersecting}. `setup-dom.ts` installs it before every test file.
 */

interface Registry {
  readonly instances: Set<FakeIntersectionObserver>;
}

const REGISTRY_KEY = '__egptIntersectionObserverRegistry';

/** On `globalThis`: the setup file and a spec can load distinct copies of this module. */
function registry(): Registry {
  const global = globalThis as Record<string, unknown>;
  global[REGISTRY_KEY] ??= { instances: new Set() } satisfies Registry;
  return global[REGISTRY_KEY] as Registry;
}

/**
 * Reports only what a spec tells it, never on `observe`: code that is wrong until a real
 * observer's async first record arrives should fail here.
 */
export class FakeIntersectionObserver {
  readonly targets = new Set<Element>();
  disconnected = false;

  constructor(
    readonly callback: IntersectionObserverCallback,
    readonly options?: IntersectionObserverInit,
  ) {
    registry().instances.add(this);
  }

  get root(): IntersectionObserverInit['root'] {
    return this.options?.root;
  }

  get rootMargin(): string {
    return this.options?.rootMargin ?? '';
  }

  observe(target: Element): void {
    this.targets.add(target);
  }

  unobserve(target: Element): void {
    this.targets.delete(target);
  }

  disconnect(): void {
    this.targets.clear();
    this.disconnected = true;
  }

  takeRecords(): IntersectionObserverEntry[] {
    return [];
  }
}

const INSTALLED = '__egptFakeIntersectionObserver';

/** Idempotent, so a spec may call it directly without fighting the global setup. */
export function installIntersectionObserver(): void {
  if (typeof globalThis === 'undefined') {
    return;
  }

  const scope = globalThis as unknown as Record<string, Record<string, unknown> | undefined>;
  if (scope['IntersectionObserver']?.[INSTALLED] === true) {
    return;
  }

  const fake = FakeIntersectionObserver as unknown as Record<string, unknown>;
  fake[INSTALLED] = true;
  scope['IntersectionObserver'] = fake;
}

/** Every observer constructed since the last reset. */
export function intersectionObservers(): readonly FakeIntersectionObserver[] {
  return [...registry().instances];
}

/** The observer watching `target`, or undefined — the common single-target case. */
export function observerFor(target: Element): FakeIntersectionObserver | undefined {
  return intersectionObservers().find(
    (observer) => !observer.disconnected && observer.targets.has(target),
  );
}

/** Reports `target` as intersecting or not, to every observer watching it. */
export function setIntersecting(target: Element, isIntersecting: boolean): void {
  for (const observer of intersectionObservers()) {
    if (observer.disconnected || !observer.targets.has(target)) {
      continue;
    }

    const entry = { target, isIntersecting } as IntersectionObserverEntry;
    observer.callback([entry], observer as unknown as IntersectionObserver);
  }
}

/** Forgets every observer. Call between tests. */
export function resetIntersectionObservers(): void {
  registry().instances.clear();
}
