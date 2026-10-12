// Runs before every test file via the unit-test target's `setupFiles`. Kept apart from
// `test-providers.ts`: these patch APIs jsdom lacks rather than configure DI.
import { afterEach } from 'vitest';
import { installIntersectionObserver } from './intersection-observer';
import { installMatchMedia, resetMediaQueries } from './media-query';
import { installResizeObserver } from './resize-observer';

installMatchMedia();
installIntersectionObserver();
installResizeObserver();

// The media-query registry lives on `globalThis`, so a viewport one test leaves behind would
// decide the layout of whichever test the runner schedules next, even in another file.
afterEach(resetMediaQueries);
