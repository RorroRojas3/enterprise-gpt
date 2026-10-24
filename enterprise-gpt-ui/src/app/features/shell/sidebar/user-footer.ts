import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Auth } from '@core/auth/auth';
import { SessionStore } from '@core/session/session-store';
import { ThemeToggle } from '@core/theme/theme-toggle';
import { initialsOf } from '@shared/avatar/initials';
import { Icon } from '@shared/icons/icon';

/**
 * Injects its stores rather than taking inputs: it is a singleton, so every host would only
 * re-wire the same calls. Sign-out skips confirmation because conversations live on the server.
 */
@Component({
  selector: 'app-user-footer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, ThemeToggle],
  templateUrl: './user-footer.html',
  host: {
    class: 'd-flex align-items-center py-2 border-top',
    '[class.gap-2]': '!compact()',
    '[class.px-3]': '!compact()',
    '[class.flex-column]': 'compact()',
    '[class.gap-1]': 'compact()',
  },
})
export class UserFooter {
  private readonly _auth = inject(Auth);
  protected readonly session = inject(SessionStore);

  /**
   * Stacks into the collapsed sidebar's 60px strip, dropping the name. Theme and sign-out stay:
   * the collapse persists, so hiding them would force an expand just to sign out.
   */
  readonly compact = input<boolean>(false);

  /** Own markup, not the shared avatar: it is sized and coloured off the sidebar. */
  protected readonly initials = computed(() => initialsOf(this.session.displayName()));

  protected readonly signingOut = this._auth.isSigningOut;

  protected signOut(): void {
    void this._auth.signOut();
  }
}
