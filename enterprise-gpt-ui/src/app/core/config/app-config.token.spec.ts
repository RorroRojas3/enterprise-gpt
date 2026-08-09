import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import devConfig from '../../../../public/config.json';
import { AppConfig, assertAppConfig, normalizeAppConfig } from './app-config';
import { APP_CONFIG, provideAppConfig } from './app-config.token';

function config(): AppConfig {
  const raw = structuredClone(devConfig) as unknown;
  assertAppConfig(raw);

  return normalizeAppConfig(raw);
}

describe('APP_CONFIG', () => {
  it('provides the configuration through provideAppConfig', () => {
    const expected = config();
    TestBed.configureTestingModule({ providers: [provideAppConfig(expected)] });

    expect(TestBed.inject(APP_CONFIG)).toEqual(expected);
  });

  it('throws when no provider is registered', () => {
    // The token carries no factory on purpose: a silent default would point the
    // app at the wrong environment instead of failing.
    TestBed.configureTestingModule({ providers: [] });

    expect(() => TestBed.inject(APP_CONFIG)).toThrow();
  });
});
