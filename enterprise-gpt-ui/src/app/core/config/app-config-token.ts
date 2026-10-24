import { EnvironmentProviders, InjectionToken, makeEnvironmentProviders } from '@angular/core';
import { AppConfig } from './app-config';

/**
 * Runtime configuration loaded before bootstrap. No `factory`, so a missing provider fails at
 * injection rather than defaulting to the wrong environment.
 */
export const APP_CONFIG = new InjectionToken<AppConfig>('APP_CONFIG');

/** For tests and injectors created outside bootstrap; `main.ts` provides the token directly. */
export function provideAppConfig(config: AppConfig): EnvironmentProviders {
  return makeEnvironmentProviders([{ provide: APP_CONFIG, useValue: config }]);
}
