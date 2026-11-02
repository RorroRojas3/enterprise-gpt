import {
  EnvironmentProviders,
  Injector,
  afterNextRender,
  inject,
  provideAppInitializer,
} from '@angular/core';
import { NavigationEnd, NavigationError, NavigationSkipped, Router } from '@angular/router';
import { filter, take } from 'rxjs';
import { hideStartupShell, showFatalShell } from './fatal-shell';

/**
 * Removes the startup shell in the render after the first settled navigation: on bootstrap an
 * empty `<app-root>` would sit above it, and `whenStable()` waits out every HTTP request.
 * Not on `NavigationCancel`, which means a redirect follows or the browser is leaving for Entra.
 * A failed first navigation has nothing to hand over to, so the shell says so instead.
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
      .subscribe((event) => {
        // After the render either way: this runs before bootstrap, which needs `<app-root>`.
        afterNextRender(
          () =>
            event instanceof NavigationError
              ? showFatalShell('The application could not be loaded. Check your connection.')
              : hideStartupShell(),
          { injector },
        );
      });
  });
}
