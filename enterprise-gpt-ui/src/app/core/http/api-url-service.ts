import { Injectable, inject } from '@angular/core';
import { APP_CONFIG } from '../config/app-config.token';

/**
 * A service rather than a function calling `inject()`, because URLs are mostly built in store
 * methods and interceptors, outside an injection context.
 */
@Injectable({ providedIn: 'root' })
export class ApiUrl {
  private readonly _baseUrl = inject(APP_CONFIG).apiBaseUrl;

  /** Interpolate identifiers into `path` with {@link segment} rather than concatenating them. */
  build(path: string): string {
    return `${this._baseUrl}/api/${path.replace(/^\/+/, '')}`;
  }

  /**
   * Decides where `authInterceptor` and the chat stream attach a bearer token. The explicit `/`
   * stops a lookalike host that merely begins with the base URL from matching.
   */
  owns(url: string): boolean {
    return url === this._baseUrl || url.startsWith(`${this._baseUrl}/`);
  }

  static segment(value: string): string {
    return encodeURIComponent(value);
  }
}
