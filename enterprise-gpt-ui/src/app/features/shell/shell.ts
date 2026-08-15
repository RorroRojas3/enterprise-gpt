import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { UiStore } from '@core/ui/ui-store';
import { DeleteConversationDialog } from './conversation-actions/delete-conversation-dialog';
import { RenameConversationDialog } from './conversation-actions/rename-conversation-dialog';
import { DeleteProjectDialog } from './project-actions/delete-project-dialog';
import { ProjectFormDialog } from './project-actions/project-form-dialog';
import { Sidebar } from './sidebar/sidebar';

/**
 * The signed-in application frame: the sidebar, and the routed screen beside it.
 *
 * A layout route rather than markup in `App`, because the four unguarded routes —
 * `/auth`, `/login-failed`, `/session-error`, `/signed-out` — and the forbidden page of
 * frame `6e` are all deliberately chrome-free, and a shell in `App` would put a sidebar
 * around every one of them.
 */
@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DeleteConversationDialog,
    DeleteProjectDialog,
    ProjectFormDialog,
    RenameConversationDialog,
    RouterOutlet,
    Sidebar,
  ],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
  host: {
    '[class.shell--collapsed]': 'collapsed()',
    '[class.shell--animated]': 'animated()',
  },
})
export class Shell {
  private readonly _ui = inject(UiStore);
  private readonly _animated = signal(false);
  private readonly _main = viewChild.required<ElementRef<HTMLElement>>('main');

  protected readonly collapsed = this._ui.sidebarCollapsed;
  protected readonly animated = this._animated.asReadonly();

  /** Moves focus into the screen without letting the fragment reach the router. */
  protected skipToMain(event: Event): void {
    event.preventDefault();
    this._main().nativeElement.focus();
  }

  constructor() {
    // The width transition is withheld until after the first render. `UiStore` restores
    // the collapsed preference before anything paints, so without this a returning user
    // would watch their sidebar animate from 260px to 60px on every load — an animation
    // of a state change that never happened.
    afterNextRender(() => this._animated.set(true));
  }
}
