import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnInit,
  inject,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '@core/auth/auth-service';
import { LOGIN_FAILED_ROUTE } from '@core/auth/auth-routes';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { Ridgeline } from '@shared/ridgeline/ridgeline';
import { focusOnRender } from '../focus-on-render';
import { AuthPage } from '../auth-page/auth-page';

/**
 * The MSAL redirect target — frame `6a`, and frame `6f` in dark.
 *
 * This route is deliberately unguarded. It *is* the redirect URI, so a guard here
 * would either recurse or block the very handshake it is waiting on, and an async
 * guard would hold the navigation open and suppress the interstitial the acceptance
 * criterion requires to be on screen while the redirect resolves.
 *
 * `AuthService.ready()` is memoized, so calling it here and from `authGuard` performs
 * one handshake.
 */
@Component({
  selector: 'app-auth-callback',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AuthPage, BrandLogo, Ridgeline],
  templateUrl: './auth-callback.html',
  styleUrl: './auth-callback.scss',
})
export class AuthCallback implements OnInit {
  private readonly _auth = inject(AuthService);
  private readonly _router = inject(Router);
  private readonly _heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  constructor() {
    focusOnRender(this._heading);
  }

  ngOnInit(): void {
    void this.complete();
  }

  private async complete(): Promise<void> {
    const { account, error } = await this._auth.ready();

    // replaceUrl is not cosmetic. It drops `/auth#code=…` from history, so the back
    // button cannot replay a consumed authorization code, and the code stops
    // travelling in any URL that later gets logged or shared.
    await this._router.navigateByUrl(this.destination(account !== null, error !== null), {
      replaceUrl: true,
    });
  }

  private destination(signedIn: boolean, failed: boolean): string {
    if (failed) {
      return LOGIN_FAILED_ROUTE;
    }

    // Neither an account nor an error is a real outcome: MSAL swallows some malformed
    // responses and resolves null without throwing. Passing such a visitor onward to a
    // guarded route starts an `/auth` → `/chat` → Entra → `/auth` loop whose only
    // visible state is the startup shell, so the second occurrence stops rather than
    // tries again. The benign case — someone opening `/auth` directly — takes the same
    // branch, which is why the first occurrence is still allowed through.
    if (!signedIn && this._auth.noteInconclusiveHandshake()) {
      return LOGIN_FAILED_ROUTE;
    }

    return this._auth.consumeReturnUrl();
  }
}
