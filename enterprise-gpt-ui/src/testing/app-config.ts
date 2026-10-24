import { EnvironmentProviders } from '@angular/core';
import devConfig from '../../public/config.json';
import { AppConfig, assertAppConfig, normalizeAppConfig } from '../app/core/config/app-config';
import { provideAppConfig } from '../app/core/config/app-config-token';

/** The real dev `config.json`, validated and normalized as `main.ts` does, so drift fails specs. */
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
