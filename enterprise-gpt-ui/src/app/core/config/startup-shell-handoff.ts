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
 * Hands the page over from `index.html`'s startup shell to the rendered application.
 *
 * The shell cannot simply be removed when `bootstrapApplication` resolves. It is a
 * block in normal flow rather than an overlay, so between bootstrap and the first
 * routed render there would be an empty `<app-root>` sitting above a "Starting
 * Enterprise GPT…" card — the "empty app shell" US-201 rules out. Waiting for
 * `ApplicationRef.whenStable()` instead overshoots badly: every `HttpClient` request
 * registers a pending task, so the shell would stay up for the whole session
 * bootstrap and both catalog loads.
 *
 * The right moment is the first settled navigation, one render later. `afterNextRender`
 * puts the removal in the same task as the render that replaces it, so neither a gap
 * nor an overlap is ever painted.
 *
 * `NavigationCancel` is deliberately not a trigger. A cancel means either a redirect —
 * a second navigation is already coming, and hiding now would flash — or a guard that
 * refused, which in this application happens only once `authGuard` has committed the
 * browser to leaving for Entra. Keeping the shell up is correct in both cases.
 */
export function provideStartupShellHandoff(): EnvironmentProviders {
  return provideAppInitializer(() => {
    const router = inject(Router);
    // Captured while the initializer still has an injection context; the subscription
    // callback runs without one, and afterNextRender needs an injector to bind to.
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
