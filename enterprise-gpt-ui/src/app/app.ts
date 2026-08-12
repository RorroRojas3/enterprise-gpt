import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AuthService } from '@core/auth/auth-service';
import { SigningOut } from '@shared/feedback/signing-out/signing-out';
import { ToastRegion } from '@shared/feedback/toast/toast-region';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, SigningOut, ToastRegion],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Deliberately almost empty. The signed-in chrome — sidebar, user footer, theme
  // control — belongs to `Shell`, a layout route under `sessionGuard`, because the
  // four unguarded routes (/auth, /login-failed, /session-error, /signed-out) and the
  // forbidden page are all full-page states that must render with no chrome at all.
  //
  // <app-toast-region> is mounted here permanently, and once: its two live regions have
  // to exist in the DOM before the first toast, or the first one is never announced.
  //
  // The sign-out interstitial replaces the whole routed area rather than being a route
  // of its own, because a navigation is precisely what sign-out must not do: the guards
  // are latched off and MSAL is mid-redirect. Swapping it in here is also what keeps
  // the emptied stores off the screen — this component is OnPush under zoneless change
  // detection, so the latch and the state reset that follow one another in
  // `AuthService.signOut` settle into a single render, and no screen ever paints a
  // signed-in shell with a cleared SessionStore behind it.
  template: `
    @if (auth.isSigningOut()) {
      <app-signing-out />
    } @else {
      <router-outlet />
    }
    <app-toast-region />
  `,
})
export class App {
  protected readonly auth = inject(AuthService);
}
