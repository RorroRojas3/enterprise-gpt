import { InjectionToken } from '@angular/core';

/**
 * The `XMLHttpRequest` factory the upload transport uses.
 *
 * A DI token for the same reason `STREAM_FETCH` is one: the transport's specs
 * substitute a fake through a provider override, scoped and typed, rather than
 * monkey-patching a global that then leaks between specs.
 *
 * A factory rather than an instance, because an `XMLHttpRequest` is single-use — the
 * 401 replay needs a second one.
 */
export const UPLOAD_XHR = new InjectionToken<() => XMLHttpRequest>('UPLOAD_XHR', {
  providedIn: 'root',
  factory: () => () => new XMLHttpRequest(),
});
