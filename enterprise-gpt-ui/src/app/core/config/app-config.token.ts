import { EnvironmentProviders, InjectionToken, makeEnvironmentProviders } from '@angular/core';
import { AppConfig } from './app-config';

/**
 * The runtime configuration loaded before bootstrap.
 *
 * Carries no `factory`: a missing provider must fail loudly at injection rather
 * than hand out a default that silently points at the wrong environment.
 */
export const APP_CONFIG = new InjectionToken<AppConfig>('APP_CONFIG');

/**
 * Provides {@link APP_CONFIG}. Used by tests and by any injector created outside
 * the bootstrap path; `main.ts` provides the token directly.
 *
 * @param config The loaded configuration.
 */
export function provideAppConfig(config: AppConfig): EnvironmentProviders {
  return makeEnvironmentProviders([{ provide: APP_CONFIG, useValue: config }]);
}
