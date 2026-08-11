import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { AuthService } from '@core/auth/auth-service';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { HardNavigation } from '@core/navigation/hard-navigation';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { Ridgeline } from '@shared/ridgeline/ridgeline';
import { focusOnRender } from '@shared/a11y/focus-on-render';
import { AuthPage } from '../auth-page/auth-page';

/**
 * Where sign-out lands — the successful end-session round trip and the failure
 * fallback both.
 *
 * Unguarded, and that is the entire point rather than a convenience. `authGuard` on
 * this route would start a sign-in immediately, and after a *failed* sign-out the Entra
 * session cookie is still live, so Entra would silently re-authenticate the person who
 * just walked away from the machine. Signing in again has to be a deliberate press.
 *
 * It is not one of the `6x` frames; the design puts sign-out in the sidebar footer and
 * says nothing about where it lands. This reuses `AuthPage` so the family reads as one.
 */
@Component({
  selector: 'app-signed-out',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AuthPage, BrandLogo, Ridgeline],
  templateUrl: './signed-out.html',
  styleUrl: './signed-out.scss',
})
export class SignedOut {
  private readonly _auth = inject(AuthService);
  private readonly _navigation = inject(HardNavigation);
  private readonly _heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  /** Latches so a second press cannot start a second redirect over the first. */
  protected readonly signingIn = signal(false);

  constructor() {
    focusOnRender(this._heading);
  }

  protected async signIn(): Promise<void> {
    this.signingIn.set(true);

    try {
      await this._auth.signIn(CHAT_ROUTE);
    } catch {
      // The browser is still here, so the redirect never started. A reload is the only
      // recovery: MSAL takes its interaction lock before navigating and exposes no way
      // to release it in-document, and this page — unlike `/login-failed` — has not run
      // a handshake that could clear it.
      this._navigation.reload();
    }
  }
}
