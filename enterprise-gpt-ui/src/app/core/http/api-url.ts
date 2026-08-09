import { Injectable, inject } from '@angular/core';
import { APP_CONFIG } from '../config/app-config.token';

/**
 * Builds absolute API URLs from the runtime configuration.
 *
 * A service rather than a bare function, because a function calling `inject()`
 * would only be legal inside an injection context — and a URL built from a
 * runtime value is almost always built inside a store method, a resolver, or an
 * interceptor, which is exactly where `inject()` throws NG0203.
 */
@Injectable({ providedIn: 'root' })
export class ApiUrl {
  private readonly _baseUrl = inject(APP_CONFIG).apiBaseUrl;

  /**
   * Builds an absolute URL for a route relative to `api/`.
   *
   * @param path A route such as `conversations/search`. Interpolate identifiers
   *   with {@link segment} rather than concatenating them raw.
   */
  build(path: string): string {
    return `${this._baseUrl}/api/${path.replace(/^\/+/, '')}`;
  }

  /**
   * Escapes a value for use as a single path segment.
   *
   * @param value The identifier to encode.
   */
  static segment(value: string): string {
    return encodeURIComponent(value);
  }
}
