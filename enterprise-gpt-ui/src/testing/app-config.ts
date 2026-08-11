import { EnvironmentProviders } from '@angular/core';
import devConfig from '../../public/config.json';
import { AppConfig, assertAppConfig, normalizeAppConfig } from '../app/core/config/app-config';
import { provideAppConfig } from '../app/core/config/app-config.token';

/**
 * The committed dev `config.json`, validated and normalized the way `main.ts` does.
 *
 * Reusing the real file rather than inventing a literal means a spec fails when the
 * shape drifts, which is the same guarantee `app-config.token.spec.ts` relies on.
 *
 * @param overrides Fields this spec needs to differ.
 */
export function testAppConfig(overrides: Partial<AppConfig> = {}): AppConfig {
  const raw = structuredClone(devConfig) as unknown;
  assertAppConfig(raw);

  return normalizeAppConfig({ ...raw, ...overrides });
}

/** Provides {@link testAppConfig} for a `TestBed`. */
export function provideTestAppConfig(overrides: Partial<AppConfig> = {}): EnvironmentProviders {
  return provideAppConfig(testAppConfig(overrides));
}

/** The API base URL every spec builds request URLs from. */
export const TEST_API_BASE_URL = testAppConfig().apiBaseUrl;
