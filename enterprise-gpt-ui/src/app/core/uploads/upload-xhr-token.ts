import { InjectionToken } from '@angular/core';

/**
 * A token so specs can substitute a fake without patching a global; a factory because an
 * `XMLHttpRequest` is single-use and the 401 replay needs a second one.
 */
export const UPLOAD_XHR = new InjectionToken<() => XMLHttpRequest>('UPLOAD_XHR', {
  providedIn: 'root',
  factory: () => () => new XMLHttpRequest(),
});
