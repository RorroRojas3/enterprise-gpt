import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AuthService } from '@core/auth/auth-service';
import { SessionStore } from '@core/session/session-store';
import { SigningOut } from '@shared/feedback/signing-out/signing-out';
import { ToastRegion } from '@shared/feedback/toast/toast-region';
import { UserFooter } from '@shared/nav/user-footer/user-footer';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, SigningOut, ToastRegion, UserFooter],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The header is a temporary host for the user footer, which carries the theme control
  // as well, so US-105 and US-205 are verifiable before a shell exists. US-301 moves
  // <app-user-footer> into the sidebar footer, where the board puts it, and deletes the
  // header. It is gated on a loaded session because the four unguarded routes — /auth,
  // /login-failed, /session-error, /signed-out — have none: an ungated footer puts an
  // empty avatar, a blank name and a live Sign out button over a frame whose whole
  // purpose is to be chrome-free.
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
      @if (session.isLoaded()) {
        <header class="dev-shell-bar"><app-user-footer /></header>
      }
      <router-outlet />
    }
    <app-toast-region />
  `,
  styles: `
    /* Fixed rather than in flow: the auth pages are full-height and centred, and a
       header taking layout space would push their content off centre and add a
       scrollbar. Temporary either way — see the note above. */
    .dev-shell-bar {
      position: fixed;
      z-index: 10;
      top: 0;
      right: 0;
      display: flex;
      justify-content: flex-end;
      padding: 10px 16px;
    }
  `,
})
export class App {
  protected readonly auth = inject(AuthService);
  protected readonly session = inject(SessionStore);
}
