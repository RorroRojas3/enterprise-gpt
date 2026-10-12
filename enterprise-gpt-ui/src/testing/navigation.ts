import { Provider } from '@angular/core';
import { vi } from 'vitest';
import { HardNavigation } from '../app/core/navigation/hard-navigation-service';

/** A {@link HardNavigation} whose two methods are spies rather than real navigations. */
export type FakeNavigation = {
  readonly assign: ReturnType<typeof vi.fn<(url: string) => void>>;
  readonly reload: ReturnType<typeof vi.fn<() => void>>;
};

/** A recording {@link HardNavigation}, since jsdom's `Location` cannot be patched. */
export function provideFakeNavigation(): Provider & { useValue: FakeNavigation } {
  return {
    provide: HardNavigation,
    useValue: { assign: vi.fn<(url: string) => void>(), reload: vi.fn<() => void>() },
  };
}
