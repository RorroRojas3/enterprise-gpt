import {
  EnvironmentProviders,
  Provider,
  provideCheckNoChangesConfig,
  provideZonelessChangeDetection,
} from '@angular/core';

/**
 * Providers installed into every unit test, wired through the `providersFile`
 * option of the `@angular/build:unit-test` target.
 *
 * The exhaustive check-no-changes pass makes a view that reads mutated plain
 * object state fail immediately rather than in a browser: in a zoneless app
 * nothing re-renders without a notification, and that class of bug is otherwise
 * invisible until someone looks at the screen.
 */
const testProviders: (Provider | EnvironmentProviders)[] = [
  // Stated rather than inferred, mirroring app.config.ts. Without zone.js in the
  // polyfills TestBed already defaults to zoneless, but the model a test runs
  // under should be readable here rather than deduced from an absent entry.
  provideZonelessChangeDetection(),
  provideCheckNoChangesConfig({ exhaustive: true }),
];

export default testProviders;
