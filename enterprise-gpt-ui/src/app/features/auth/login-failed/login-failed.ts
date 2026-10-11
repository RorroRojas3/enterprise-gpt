import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnInit,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { AuthService } from '@core/auth/auth-service';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { HardNavigation } from '@core/navigation/hard-navigation';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { focusOnRender } from '@shared/a11y/focus-on-render';
import { AuthPage } from '../auth-page/auth-page';

/** Unguarded: a guard would send a user whose sign-in just failed straight back to Entra. */
@Component({
  selector: 'app-login-failed',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AuthPage, BrandLogo],
  templateUrl: './login-failed.html',
  styleUrl: './login-failed.scss',
})
export class LoginFailed implements OnInit {
  private readonly _auth = inject(AuthService);
  private readonly _navigation = inject(HardNavigation);
  private readonly _heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  /** Latches so a second press cannot start a second redirect over the first. */
  protected readonly retrying = signal(false);

  /** Set when the retry could not reach Entra, so the user is told rather than left waiting. */
  protected readonly retryFailed = signal(false);

  constructor() {
    focusOnRender(this._heading);
  }

  ngOnInit(): void {
    // Unguarded, so no guard has run the handshake that releases a stale interaction lock.
    void this._auth.ready();
  }

  protected async retry(): Promise<void> {
    // MSAL's lock has no release API and this page's memoized handshake already ran, so after
    // a failed redirect only a fresh document can try again.
    if (this.retryFailed()) {
      this._navigation.reload();

      return;
    }

    this.retrying.set(true);

    try {
      await this._auth.signIn(CHAT_ROUTE);
    } catch {
      // The browser is still here, so the redirect never started.
      this.retrying.set(false);
      this.retryFailed.set(true);
    }
  }
}
