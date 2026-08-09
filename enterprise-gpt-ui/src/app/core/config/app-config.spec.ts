import { describe, expect, it } from 'vitest';
import devConfig from '../../../../public/config.json';
import { AppConfig, AppConfigError, assertAppConfig, normalizeAppConfig } from './app-config';

function validConfig(): Record<string, unknown> {
  return structuredClone(devConfig) as Record<string, unknown>;
}

describe('assertAppConfig', () => {
  it('accepts the config.json committed to the repository', () => {
    expect(() => assertAppConfig(validConfig())).not.toThrow();
  });

  it('accepts unknown extra keys so a newer config boots an older bundle', () => {
    const config = { ...validConfig(), telemetry: { enabled: true } };

    expect(() => assertAppConfig(config)).not.toThrow();
  });

  it.each([
    ['null', null],
    ['an array', []],
    ['a string', 'x'],
    ['a number', 7],
    ['undefined', undefined],
  ])('rejects %s at the root', (_label, value) => {
    expect(() => assertAppConfig(value)).toThrow(AppConfigError);
  });

  it.each([
    ['apiBaseUrl', (c: Record<string, unknown>) => delete c['apiBaseUrl']],
    ['apiBaseUrl', (c: Record<string, unknown>) => (c['apiBaseUrl'] = 'not a url')],
    ['apiBaseUrl', (c: Record<string, unknown>) => (c['apiBaseUrl'] = 'ftp://example.com')],
    ['apiBaseUrl', (c: Record<string, unknown>) => (c['apiBaseUrl'] = '   ')],
    ['auth', (c: Record<string, unknown>) => delete c['auth']],
    ['auth.clientId', (c: Record<string, unknown>) => (auth(c)['clientId'] = 'not-a-guid')],
    ['auth.clientId', (c: Record<string, unknown>) => delete auth(c)['clientId']],
    [
      'auth.authority',
      (c: Record<string, unknown>) => (auth(c)['authority'] = 'http://login.example'),
    ],
    ['auth.redirectUri', (c: Record<string, unknown>) => (auth(c)['redirectUri'] = '')],
    [
      'auth.postLogoutRedirectUri',
      (c: Record<string, unknown>) => delete auth(c)['postLogoutRedirectUri'],
    ],
    ['auth.apiScopes', (c: Record<string, unknown>) => (auth(c)['apiScopes'] = [])],
    ['auth.apiScopes[0]', (c: Record<string, unknown>) => (auth(c)['apiScopes'] = [''])],
    ['features', (c: Record<string, unknown>) => delete c['features']],
    ['features.diagrams', (c: Record<string, unknown>) => (features(c)['diagrams'] = 'false')],
    ['features.math', (c: Record<string, unknown>) => delete features(c)['math']],
    [
      'features.rawStreamCodec',
      (c: Record<string, unknown>) => (features(c)['rawStreamCodec'] = 1),
    ],
  ])('rejects a bad %s and names the field', (field, mutate) => {
    const config = validConfig();
    mutate(config);

    // The message is what the fatal shell renders, so an unnamed field would leave
    // whoever has to fix the deployment with nothing to go on.
    expect(() => assertAppConfig(config)).toThrow(new RegExp(`"${escapeRegExp(field)}"`));
  });
});

describe('normalizeAppConfig', () => {
  it('strips a trailing slash from apiBaseUrl', () => {
    const config = { ...validConfig(), apiBaseUrl: 'https://api.example.com/' };
    assertAppConfig(config);

    expect(normalizeAppConfig(config).apiBaseUrl).toBe('https://api.example.com');
  });

  it('freezes the result and its nested objects', () => {
    const config = validConfig();
    assertAppConfig(config);
    const normalized: AppConfig = normalizeAppConfig(config);

    expect(Object.isFrozen(normalized)).toBe(true);
    expect(Object.isFrozen(normalized.auth)).toBe(true);
    expect(Object.isFrozen(normalized.features)).toBe(true);
  });
});

function auth(config: Record<string, unknown>): Record<string, unknown> {
  return config['auth'] as Record<string, unknown>;
}

function features(config: Record<string, unknown>): Record<string, unknown> {
  return config['features'] as Record<string, unknown>;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
