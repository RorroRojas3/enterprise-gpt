import { InjectionToken } from '@angular/core';

/**
 * The streaming transport's `fetch`, a token so specs can override it without a global stub.
 * Wrapped in a lambda because an extracted `window.fetch` called bare is an illegal invocation.
 */
export const STREAM_FETCH = new InjectionToken<typeof fetch>('STREAM_FETCH', {
  providedIn: 'root',
  factory: () => (input, init) => globalThis.fetch(input, init),
});
