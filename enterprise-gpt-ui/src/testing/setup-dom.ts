// Global test setup, wired through the `setupFiles` option of the
// `@angular/build:unit-test` target. It runs before every test file.
//
// Separate from `test-providers.ts`, whose contract is a default-exported provider
// array: these are prototype-level patches for APIs jsdom does not implement, not
// dependency-injection configuration.
import { afterEach } from 'vitest';

import { installIntersectionObserver } from './intersection-observer';
import { installMatchMedia, resetMediaQueries } from './media-query';
import { installResizeObserver } from './resize-observer';

installMatchMedia();
installIntersectionObserver();
installResizeObserver();

// The media-query registry lives on `globalThis`, so a test that ends at a viewport it
// set decides the layout every test after it renders at — including in another file, if
// the runner shares an environment. That is invisible until a spec asserting on a
// desktop-only column happens to be scheduled behind one that ended at tablet width,
// which is a sharding decision rather than anything either spec says. Resetting here
// costs a `Map.clear()` and makes the starting viewport a fact instead of a race.
afterEach(resetMediaQueries);
