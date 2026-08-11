import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { AuthService } from '@core/auth/auth-service';
import { SessionStore } from '@core/session/session-store';
import { Icon } from '@shared/icon/icon';
import { ThemeToggle } from '@shared/theme-toggle/theme-toggle';

/**
 * Frame `3a`'s sidebar footer — who is signed in, the theme control, and sign out.
 *
 * It injects `SessionStore` and `AuthService` from `core/`, which `shared/` is allowed
 * to do and `ThemeToggle` already does. The alternative — inputs and an output threaded
 * down from whichever screen hosts it — would make every future host re-derive the
 * initials and re-wire the sign-out call, for a component that is a singleton by
 * construction.
 *
 * US-301 moves this into the sidebar, where the collapsed 60px strip shows the avatar
 * alone. Until then it lives in the temporary shell bar, exactly as `ThemeToggle` does.
 *
 * Sign-out asks for no confirmation, matching the board. Nothing is lost by signing
 * out — conversations are on the server — so a dialog would be friction guarding
 * nothing.
 */
@Component({
  selector: 'app-user-footer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, ThemeToggle],
  templateUrl: './user-footer.html',
  styleUrl: './user-footer.scss',
})
export class UserFooter {
  private readonly _auth = inject(AuthService);
  protected readonly session = inject(SessionStore);

  /**
   * First letters of the first and last name. Derived from `fullName` rather than the
   * separate fields because that is the one the server has already composed, and a
   * single-word name has to fall back to one letter rather than render a stray comma.
   */
  protected readonly initials = computed(() =>
    this.session
      .displayName()
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((part) => part[0]?.toUpperCase() ?? '')
      .join(''),
  );

  protected readonly signingOut = this._auth.isSigningOut;

  protected signOut(): void {
    void this._auth.signOut();
  }
}
