import { Provider } from '@angular/core';
import { vi } from 'vitest';
import { HardNavigation } from '../app/core/navigation/hard-navigation';

/** A {@link HardNavigation} whose two methods are spies rather than real navigations. */
export type FakeNavigation = {
  readonly assign: ReturnType<typeof vi.fn<(url: string) => void>>;
  readonly reload: ReturnType<typeof vi.fn<() => void>>;
};

/**
 * Provides a recording {@link HardNavigation}.
 *
 * jsdom's `Location` is not patchable, so a spec that let the real service through
 * would emit a "Not implemented: navigation" from the virtual console and assert
 * nothing.
 */
export function provideFakeNavigation(): Provider & { useValue: FakeNavigation } {
  return {
    provide: HardNavigation,
    useValue: { assign: vi.fn<(url: string) => void>(), reload: vi.fn<() => void>() },
  };
}
