import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { loadIconSprite } from './load-icon-sprite';

const SPRITE = '<svg xmlns="http://www.w3.org/2000/svg"><symbol id="bi-x-lg"></symbol></svg>';

function respondWith(body: string, ok = true, status = 200): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => ({ ok, status, text: async () => body }) as unknown as Response),
  );
}

describe('loadIconSprite', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('injects the sprite so <use href="#bi-…"> resolves same-document', async () => {
    respondWith(SPRITE);

    await loadIconSprite();

    expect(document.querySelector('symbol#bi-x-lg')).not.toBeNull();
    expect(document.body.firstElementChild?.getAttribute('aria-hidden')).toBe('true');
  });

  it('does nothing on a 404 — icons are degraded, not fatal', async () => {
    respondWith('Not found', false, 404);

    await loadIconSprite();

    expect(document.body.children).toHaveLength(0);
  });

  it('rejects an index.html body, which SPA fallback routing returns with a 200', async () => {
    respondWith('<!doctype html><html><body><app-root></app-root></body></html>');

    await loadIconSprite();

    expect(document.querySelector('app-root')).toBeNull();
    expect(document.body.children).toHaveLength(0);
  });

  it('never rejects, so it cannot reroute bootstrap into the fatal shell', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('Failed to fetch');
      }),
    );

    await expect(loadIconSprite()).resolves.toBeUndefined();
    expect(document.body.children).toHaveLength(0);
  });
});
