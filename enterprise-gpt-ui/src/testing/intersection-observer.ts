/**
 * A controllable `IntersectionObserver` for tests.
 *
 * jsdom implements none at all, and it could not usefully implement one: there is
 * no layout, so nothing ever intersects anything. Specs drive visibility
 * explicitly with {@link setIntersecting} instead, which is the honest shape for
 * the thing under test anyway — US-606 cares only about the boolean the observer
 * reports, never about how a real one arrived at it.
 *
 * `setup-dom.ts` installs it before every test file.
 */

interface Registry {
  readonly instances: Set<FakeIntersectionObserver>;
}

const REGISTRY_KEY = '__egptIntersectionObserverRegistry';

/**
 * On `globalThis` for the same reason the media-query registry is: the setup file
 * and the spec are separate build entries and can hold distinct instances of this
 * module, which would leave a spec driving an observer the code never created.
 */
function registry(): Registry {
  const global = globalThis as Record<string, unknown>;
  global[REGISTRY_KEY] ??= { instances: new Set() } satisfies Registry;
  return global[REGISTRY_KEY] as Registry;
}

/**
 * Records what it was asked to watch and reports only what a spec tells it to.
 *
 * It deliberately does **not** fire on `observe`. A real observer delivers a first
 * record asynchronously, so code that only becomes correct once that record
 * arrives is code with a visible wrong state on screen first; leaving the fake
 * silent keeps that mistake failing here.
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
