import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnInit,
  inject,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { focusOnRender } from '@core/a11y/focus-on-render';
import { AuthService } from '@core/auth/auth-service';
import { LOGIN_FAILED_ROUTE } from '@core/navigation/route-paths';
import { BrandLogo } from '@shared/components/brand-logo/brand-logo';
import { Ridgeline } from '@shared/components/ridgeline/ridgeline';
import { AuthPage } from '@components/auth/auth-page';

/**
 * Unguarded: as the redirect URI, a guard here would recurse or block the handshake it awaits, and
 * would hold the navigation open instead of showing this interstitial.
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

    // replaceUrl drops `/auth#code=…` from history, so Back cannot replay a consumed code.
    await this._router.navigateByUrl(this.destination(account !== null, error !== null), {
      replaceUrl: true,
    });
  }

  private destination(signedIn: boolean, failed: boolean): string {
    if (failed) {
      return LOGIN_FAILED_ROUTE;
    }

    // MSAL resolves some malformed responses as null, which would loop through Entra. The first
    // occurrence still passes, because opening `/auth` directly looks the same.
    if (!signedIn && this._auth.noteInconclusiveHandshake()) {
      return LOGIN_FAILED_ROUTE;
    }

    return this._auth.consumeReturnUrl();
  }
}
