import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { focusOnRender } from '@core/a11y/focus-on-render';
import { Auth } from '@core/auth/auth';
import { HardNavigation } from '@core/navigation/hard-navigation';
import { CHAT_ROUTE } from '@core/navigation/route-paths';
import { BrandLogo } from '@shared/brand/brand-logo';
import { Ridgeline } from '@shared/brand/ridgeline';
import { AuthPage } from './auth-page';

/**
 * Unguarded: after a failed sign-out the Entra cookie is still live, so `authGuard` would silently
 * re-authenticate. Signing in again has to be a deliberate press.
 */
@Component({
  selector: 'app-signed-out',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AuthPage, BrandLogo, Ridgeline],
  host: { class: 'd-block' },
  templateUrl: './signed-out.html',
})
export class SignedOut {
  private readonly _auth = inject(Auth);
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
      // The redirect never started. Only a reload clears MSAL's interaction lock, since this page
      // ran no handshake.
      this._navigation.reload();
    }
  }
}
