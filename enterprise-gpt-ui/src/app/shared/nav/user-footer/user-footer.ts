import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
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
  host: { '[class.user-footer--compact]': 'compact()' },
})
export class UserFooter {
  private readonly _auth = inject(AuthService);
  protected readonly session = inject(SessionStore);

  /**
   * Stacks the row into the collapsed sidebar's 60px strip, dropping the display name.
   *
   * Frame `3b` draws the avatar alone there. The theme control and sign out are kept
   * anyway, as icon buttons with tooltips: a collapsed sidebar is a persisted state, so
   * hiding them would make signing out require expanding the sidebar first — and the
   * board itself gives the strip no other route to either.
   */
  readonly compact = input<boolean>(false);

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
