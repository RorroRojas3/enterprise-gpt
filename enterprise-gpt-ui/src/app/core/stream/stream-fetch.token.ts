import { InjectionToken } from '@angular/core';

/**
 * The `fetch` the streaming transport uses.
 *
 * A DI token so the transport's specs can substitute a fake with a provider
 * override — scoped and typed, unlike a global stub that can leak between
 * specs. The factory wraps `globalThis.fetch` in a lambda rather than binding
 * it: an extracted `window.fetch` called bare is an illegal invocation in
 * browsers.
 */
export const STREAM_FETCH = new InjectionToken<typeof fetch>('STREAM_FETCH', {
  providedIn: 'root',
  factory: () => (input, init) => globalThis.fetch(input, init),
});
