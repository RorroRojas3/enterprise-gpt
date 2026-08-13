// Global test setup, wired through the `setupFiles` option of the
// `@angular/build:unit-test` target. It runs before every test file.
//
// Separate from `test-providers.ts`, whose contract is a default-exported provider
// array: these are prototype-level patches for APIs jsdom does not implement, not
// dependency-injection configuration.
import { installIntersectionObserver } from './intersection-observer';
import { installMatchMedia } from './media-query';
import { installResizeObserver } from './resize-observer';

installMatchMedia();
installIntersectionObserver();
installResizeObserver();
