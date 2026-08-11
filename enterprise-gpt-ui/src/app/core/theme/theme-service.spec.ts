import { TestBed } from '@angular/core/testing';
import { PREFERENCE_KEYS, writePreference } from '@core/storage/local-preferences';
import { PREFERS_DARK, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeService } from './theme-service';

function root(): HTMLElement {
  return document.documentElement;
}

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.clear();
    resetMediaQueries();
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    // documentElement is shared across every test in the file, and colorScheme is an
    // inline style rather than an attribute.
    root().removeAttribute('data-bs-theme');
    root().style.removeProperty('color-scheme');
    vi.restoreAllMocks();
  });

  it('follows prefers-color-scheme when nothing is stored', () => {
    setMediaQuery(PREFERS_DARK, true);

    const service = TestBed.inject(ThemeService);

    expect(service.preference()).toBe('system');
    expect(service.theme()).toBe('dark');
    expect(root().getAttribute('data-bs-theme')).toBe('dark');
    expect(root().style.colorScheme).toBe('dark');
  });

  it('lets a stored preference beat the system setting', () => {
    writePreference(PREFERENCE_KEYS.theme, 'light');
    setMediaQuery(PREFERS_DARK, true);

    expect(TestBed.inject(ThemeService).theme()).toBe('light');
    expect(root().getAttribute('data-bs-theme')).toBe('light');
  });

  it('persists a choice and applies it in the same call', () => {
    const service = TestBed.inject(ThemeService);

    service.set('dark');

    expect(localStorage.getItem(PREFERENCE_KEYS.theme)).toBe('dark');
    expect(root().getAttribute('data-bs-theme')).toBe('dark');
  });

  it('toggles to the opposite of what is on screen', () => {
    setMediaQuery(PREFERS_DARK, false);
    const service = TestBed.inject(ThemeService);

    service.toggle();

    expect(service.theme()).toBe('dark');
    expect(service.preference()).toBe('dark');
  });

  it('follows a system change only while the preference is `system`', () => {
    setMediaQuery(PREFERS_DARK, false);
    const service = TestBed.inject(ThemeService);

    setMediaQuery(PREFERS_DARK, true);
    expect(service.theme()).toBe('dark');

    service.set('light');
    setMediaQuery(PREFERS_DARK, false);
    setMediaQuery(PREFERS_DARK, true);
    expect(service.theme()).toBe('light');
  });

  it('resumes following the OS when the preference returns to `system`', () => {
    const service = TestBed.inject(ThemeService);
    service.set('light');

    setMediaQuery(PREFERS_DARK, true);
    service.set('system');

    expect(service.theme()).toBe('dark');
  });

  it('degrades a corrupt stored value to `system` rather than writing it to the DOM', () => {
    writePreference(PREFERENCE_KEYS.theme, 'chartreuse');
    setMediaQuery(PREFERS_DARK, true);

    const service = TestBed.inject(ThemeService);

    expect(service.preference()).toBe('system');
    expect(root().getAttribute('data-bs-theme')).toBe('dark');
  });

  it('survives storage that throws on read — private mode, enterprise policy', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied');
    });
    setMediaQuery(PREFERS_DARK, true);

    expect(() => TestBed.inject(ThemeService)).not.toThrow();
    expect(root().getAttribute('data-bs-theme')).toBe('dark');
  });

  it('still applies the theme when storage throws on write', () => {
    const service = TestBed.inject(ThemeService);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('quota');
    });

    expect(() => service.set('dark')).not.toThrow();
    expect(root().getAttribute('data-bs-theme')).toBe('dark');
  });
});
