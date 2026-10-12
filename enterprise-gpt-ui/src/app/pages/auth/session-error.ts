import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { SessionStore } from '@core/session/session-store';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { AuthPage } from '../auth-page/auth-page';

/**
 * Unguarded: it renders precisely when `sessionGuard` cannot pass. `ErrorPanel` words the failure
 * the same way the toast would.
 */
@Component({
  selector: 'app-session-error',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AuthPage, BrandLogo, ErrorPanel],
  templateUrl: './session-error.html',
  styleUrl: './session-error.scss',
})
export class SessionError implements OnInit {
  private readonly _session = inject(SessionStore);
  private readonly _router = inject(Router);
  private readonly _injector = inject(Injector);
  private readonly _panel = viewChild(ErrorPanel);

  protected readonly error = this._session.error;
  protected readonly retrying = signal(false);
  protected readonly canRetry = computed(() => !this.retrying());

  constructor() {
    // Not `focusOnRender`: the panel is inside an `@if`, so its `viewChild.required` would throw
    // on the bookmarked path with no error.
    afterNextRender(() => this._panel()?.focusHeading(), { injector: this._injector });
  }

  ngOnInit(): void {
    // Bookmarked, or reached after the session recovered in another tab: there is no
    // failure to explain, so this page has nothing to say.
    if (this.error() === null) {
      void this._router.navigateByUrl(CHAT_ROUTE, { replaceUrl: true });
    }
  }

  protected async retry(): Promise<void> {
    this.retrying.set(true);

    // The store's only retry: retryInterceptor covers GETs alone, so a transient
    // failure on this POST is the user's to replay.
    if (await this._session.reload()) {
      await this._router.navigateByUrl(CHAT_ROUTE, { replaceUrl: true });

      return;
    }

    this.retrying.set(false);
  }
}
