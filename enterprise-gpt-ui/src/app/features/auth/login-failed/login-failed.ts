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

/**
 * Frame `6b` — the sign-in did not complete.
 *
 * Unguarded, and it has to be: guarding it would send a user whose sign-in just
 * failed straight back to Entra, which is a loop rather than a recovery.
 *
 * There is no password field here, and there is none anywhere in this application.
 * The one action re-enters the Entra ID flow.
 */
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

  /** Set when the retry could not even reach Entra, so the user is told rather than left waiting. */
  protected readonly retryFailed = signal(false);

  constructor() {
    focusOnRender(this._heading);
  }

  ngOnInit(): void {
    // Because this route is unguarded, it is the one screen no other code path has
    // resolved the handshake for — and the handshake is what releases MSAL's
    // `interaction_in_progress` lock. Without this, a user who abandoned a sign-in
    // and landed here finds Try again throwing against a lock nothing will clear
    // until the tab is closed.
    void this._auth.ready();
  }

  protected async retry(): Promise<void> {
    // Once a redirect has failed in this document, the only recovery is a new one.
    // MSAL takes its `interaction_in_progress` lock before it navigates and exposes
    // no API to release it, and the handshake that *does* release it is memoized —
    // already resolved for this page. A second in-page attempt would therefore throw
    // against a lock nothing here can clear, so the action becomes a reload, and the
    // fresh instance releases the lock on the `ready()` in ngOnInit.
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
