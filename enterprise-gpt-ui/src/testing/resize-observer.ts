/**
 * A controllable `ResizeObserver` for tests.
 *
 * jsdom implements none, and could not usefully: without layout nothing ever
 * changes size. Specs say when an element grew with {@link resizeElement}, which
 * is the honest shape for the code under test — US-606 reacts to the fact of a
 * height change, never to the number.
 *
 * `setup-dom.ts` installs it before every test file.
 */

interface Registry {
  readonly instances: Set<FakeResizeObserver>;
}

const REGISTRY_KEY = '__egptResizeObserverRegistry';

/** On `globalThis` for the reason the media-query registry is: see that file. */
function registry(): Registry {
  const global = globalThis as Record<string, unknown>;
  global[REGISTRY_KEY] ??= { instances: new Set() } satisfies Registry;
  return global[REGISTRY_KEY] as Registry;
}

export class FakeResizeObserver {
  readonly targets = new Set<Element>();
  disconnected = false;

  constructor(readonly callback: ResizeObserverCallback) {
    registry().instances.add(this);
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
}

const INSTALLED = '__egptFakeResizeObserver';

/** Idempotent, so a spec may call it directly without fighting the global setup. */
export function installResizeObserver(): void {
  const scope = globalThis as unknown as Record<string, Record<string, unknown> | undefined>;
  if (scope['ResizeObserver']?.[INSTALLED] === true) {
    return;
  }

  const fake = FakeResizeObserver as unknown as Record<string, unknown>;
  fake[INSTALLED] = true;
  scope['ResizeObserver'] = fake;
}

/** Every observer constructed since the last reset. */
export function resizeObservers(): readonly FakeResizeObserver[] {
  return [...registry().instances];
}

/** Reports that `target` changed size, to every observer watching it. */
export function resizeElement(target: Element): void {
  for (const observer of resizeObservers()) {
    if (observer.disconnected || !observer.targets.has(target)) {
      continue;
    }

    const entry = { target } as ResizeObserverEntry;
    observer.callback([entry], observer as unknown as ResizeObserver);
  }
}

/** Forgets every observer. Call between tests. */
export function resetResizeObservers(): void {
  registry().instances.clear();
}
