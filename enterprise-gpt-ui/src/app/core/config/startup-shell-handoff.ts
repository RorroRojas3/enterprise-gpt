import {
  EnvironmentProviders,
  Injector,
  afterNextRender,
  inject,
  provideAppInitializer,
} from '@angular/core';
import { NavigationEnd, NavigationError, NavigationSkipped, Router } from '@angular/router';
import { filter, take } from 'rxjs';
import { hideStartupShell } from './fatal-shell';

/**
 * Removes the startup shell in the render after the first settled navigation: on bootstrap an
 * empty `<app-root>` would sit above it, and `whenStable()` waits out every HTTP request.
 * Not on `NavigationCancel`, which means a redirect follows or the browser is leaving for Entra.
 */
export function provideStartupShellHandoff(): EnvironmentProviders {
  return provideAppInitializer(() => {
    const router = inject(Router);
    // The subscription callback runs outside the injection context afterNextRender needs.
    const injector = inject(Injector);

    router.events
      .pipe(
        filter(
          (event) =>
            event instanceof NavigationEnd ||
            event instanceof NavigationSkipped ||
            event instanceof NavigationError,
        ),
        take(1),
      )
      .subscribe(() => afterNextRender(() => hideStartupShell(), { injector }));
  });
}
