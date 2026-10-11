import {
  EnvironmentProviders,
  Provider,
  provideCheckNoChangesConfig,
  provideZonelessChangeDetection,
} from '@angular/core';

/**
 * Installed into every unit test via the unit-test target's `providersFile`. The exhaustive
 * check-no-changes pass fails a view reading mutated plain state, which zoneless never redraws.
 */
const testProviders: (Provider | EnvironmentProviders)[] = [
  // The default without zone.js, stated so the change-detection model is readable here.
  provideZonelessChangeDetection(),
  provideCheckNoChangesConfig({ exhaustive: true }),
];

export default testProviders;
