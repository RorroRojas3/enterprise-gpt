import { afterEach, describe, expect, it, vi } from 'vitest';
import devConfig from '../../../../public/config.json';
import { AppConfigError } from './app-config';
import { loadAppConfig } from './load-app-config';

function respondWith(body: string, init: { status?: number; ok?: boolean } = {}) {
  const status = init.status ?? 200;

  return vi.fn(async () => ({
    ok: init.ok ?? (status >= 200 && status < 300),
    status,
    json: async () => JSON.parse(body) as unknown,
  }));
}

function setBaseHref(href: string): void {
  document.head.querySelector('base')?.remove();
  const base = document.createElement('base');
  base.setAttribute('href', href);
  document.head.append(base);
}

afterEach(() => {
  vi.unstubAllGlobals();
  document.head.querySelector('base')?.remove();
});

describe('loadAppConfig', () => {
  it('returns the frozen, normalized configuration on success', async () => {
    vi.stubGlobal('fetch', respondWith(JSON.stringify(devConfig)));

    const config = await loadAppConfig();

    expect(config.apiBaseUrl).toBe(devConfig.apiBaseUrl);
    expect(config.auth.clientId).toBe(devConfig.auth.clientId);
    expect(Object.isFrozen(config)).toBe(true);
  });

  it('requests config.json with no-store and an abort signal', async () => {
    const fetchSpy = respondWith(JSON.stringify(devConfig));
    vi.stubGlobal('fetch', fetchSpy);

    await loadAppConfig();

    const [, init] = fetchSpy.mock.calls[0] as unknown as [URL, RequestInit];
    // A cached config is how one environment ends up pointed at another.
    expect(init.cache).toBe('no-store');
    expect(init.credentials).toBe('omit');
    // Asserted as presence rather than by exercising the ten-second timeout.
    expect(init.signal).toBeDefined();
  });

  it('resolves the URL against the document base href', async () => {
    setBaseHref('/gpt/');
    const fetchSpy = respondWith(JSON.stringify(devConfig));
    vi.stubGlobal('fetch', fetchSpy);

    await loadAppConfig();

    const [url] = fetchSpy.mock.calls[0] as unknown as [URL];
    expect(url.pathname).toBe('/gpt/config.json');
  });

  it('fails with the status when the response is not ok', async () => {
    vi.stubGlobal('fetch', respondWith('{}', { status: 404 }));

    await expect(loadAppConfig()).rejects.toThrow(/404/);
  });

  it('fails when the request never completes', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('Failed to fetch');
      }),
    );

    await expect(loadAppConfig()).rejects.toThrow(/could not be reached/i);
  });

  it('fails when a SPA fallback serves index.html with a 200', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({
        ok: true,
        status: 200,
        json: async () => {
          throw new SyntaxError('Unexpected token <');
        },
      })),
    );

    await expect(loadAppConfig()).rejects.toThrow(/not valid JSON/i);
  });

  it('fails with an AppConfigError naming the field when validation fails', async () => {
    const invalid = { ...structuredClone(devConfig), apiBaseUrl: '' };
    vi.stubGlobal('fetch', respondWith(JSON.stringify(invalid)));

    await expect(loadAppConfig()).rejects.toThrow(AppConfigError);
    await expect(loadAppConfig()).rejects.toThrow(/"apiBaseUrl"/);
  });
});
