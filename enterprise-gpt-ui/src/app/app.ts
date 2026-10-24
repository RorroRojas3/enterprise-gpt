import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { Auth } from '@core/auth/auth';
import { ToastRegion } from '@core/notifications/toast-region';
import { ToastStore } from '@core/notifications/toast-store';
import { SigningOut } from '@core/session/signing-out';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, SigningOut, ToastRegion],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Chrome lives in `Shell`: the public routes and the forbidden page render without it.
  // The toast region mounts once here so its live regions exist before the first toast.
  // Sign-out swaps the outlet instead of navigating: the guards are latched and MSAL is
  // mid-redirect, and the emptied stores leave the screen in the same render.
  template: `
    @if (auth.isSigningOut()) {
      <app-signing-out />
    } @else {
      <router-outlet />
    }
    <!-- The region reports which toast's Retry was used; the store owns what that
         means, so the handler travels with the toast rather than with this shell. -->
    <app-toast-region (retried)="toasts.retry($event)" />
  `,
})
export class App {
  protected readonly auth = inject(Auth);
  protected readonly toasts = inject(ToastStore);
}
