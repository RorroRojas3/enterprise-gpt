import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { AppConfig, assertAppConfig, normalizeAppConfig } from '@core/config/app-config';
import { provideAppConfig } from '@core/config/app-config.token';
import devConfig from '../../../../public/config.json';
import { ApiUrl } from './api-url-service';

function configWith(apiBaseUrl: string): AppConfig {
  const raw = { ...structuredClone(devConfig), apiBaseUrl } as unknown;
  assertAppConfig(raw);

  return normalizeAppConfig(raw);
}

function apiUrl(apiBaseUrl = 'https://localhost:7045'): ApiUrl {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ providers: [provideAppConfig(configWith(apiBaseUrl))] });

  return TestBed.inject(ApiUrl);
}

describe('ApiUrl', () => {
  it('joins a relative route onto the configured base', () => {
    expect(apiUrl().build('conversations/search')).toBe(
      'https://localhost:7045/api/conversations/search',
    );
  });

  it('tolerates a leading slash on the route', () => {
    expect(apiUrl().build('/models')).toBe('https://localhost:7045/api/models');
  });

  it('does not double the separator when the configured base had a trailing slash', () => {
    // normalizeAppConfig strips it; this pins the two working together.
    expect(apiUrl('https://api.example.com/').build('models')).toBe(
      'https://api.example.com/api/models',
    );
  });

  it('is usable outside an injection context once resolved', () => {
    const service = apiUrl();
    const buildLater = () => service.build('conversations/abc');

    expect(buildLater()).toBe('https://localhost:7045/api/conversations/abc');
  });

  it('escapes an identifier used as a path segment', () => {
    expect(ApiUrl.segment('a/b?c')).toBe('a%2Fb%3Fc');
  });
});
