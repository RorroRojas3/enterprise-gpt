import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ALLOWED_PREFERENCE_KEYS,
  PREFERENCE_KEYS,
  readPreference,
  writePreference,
} from './local-preferences';

describe('local preferences', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it('round-trips a value', () => {
    writePreference(PREFERENCE_KEYS.theme, 'dark');

    expect(readPreference(PREFERENCE_KEYS.theme)).toBe('dark');
  });

  it('survives storage that throws, rather than taking the app down with it', () => {
    const denied = (): never => {
      throw new DOMException('denied');
    };
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(denied);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(denied);

    expect(() => writePreference(PREFERENCE_KEYS.theme, 'dark')).not.toThrow();
    expect(readPreference(PREFERENCE_KEYS.theme)).toBeNull();
  });

  it('allows only values about this browser, never about the user', () => {
    // US-205's second criterion, stated as data. A key added here without a matching
    // entry in the story's rule — "about the browser, not about the user" — is the
    // failure this list exists to make visible in review.
    expect([...ALLOWED_PREFERENCE_KEYS].sort()).toEqual(['egpt.sidebar', 'egpt.theme']);
  });
});
