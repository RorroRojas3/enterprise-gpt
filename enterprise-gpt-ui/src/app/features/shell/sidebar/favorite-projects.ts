import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  effect,
  inject,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { ProjectSummaryDto } from '@domain/api/project';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { Icon } from '@shared/icon/icon';
import { SidebarProjectRow } from './project-row';

/**
 * The **Favorite projects** section between the nav and the conversation list (US-910).
 *
 * Its own component rather than markup inside `Sidebar` for the reason `ConversationRow`
 * is: it owns state — which projects are open — and the sidebar's stylesheet was already
 * at its per-component budget before this section existed.
 *
 * **The rows are the server's flag, not device-local pins.** US-910's first criterion
 * describes pins in `localStorage` under a "Pinned on this device" notice, for the case
 * where the backend enabler had not landed; US-909 shipped in the same batch, so that
 * state was live for no build at all and the notice was never written. Pins were the
 * wrong thing to keep there anyway: `local-preferences.ts` holds facts about the browser,
 * and a list of project ids is a fact about the user.
 *
 * They come from {@link ProjectLookupStore}, which `SessionBootstrap` has already
 * released for the picker and the project chips, so this section costs no request of its
 * own — and inherits that store's load ceiling with it. There is no error or skeleton
 * branch for the same reason: a lookup that has not arrived has no favourites to show,
 * and the picker is the surface that reports its failures.
 */
@Component({
  selector: 'app-sidebar-favorites',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, SidebarProjectRow],
  templateUrl: './favorite-projects.html',
  styleUrl: './favorite-projects.scss',
})
export class SidebarFavorites {
  private readonly _injector = inject(Injector);
  private readonly _toggle = viewChild<ElementRef<HTMLButtonElement>>('favoritesToggle');

  private readonly _actions = inject(ProjectActionsStore);

  protected readonly projects = inject(ProjectLookupStore);

  /**
   * The project whose own kebab will remove its row, so focus can be moved when it goes.
   *
   * Unfavourite and Delete both destroy the `<li>` holding the menu trigger that focus
   * was returned to when the panel closed, roughly a round trip later — and `Menu`'s own
   * rescue cannot help, because it is destroyed along with the row. The same shape the
   * conversations library solves for its own self-evicting star.
   */
  private readonly _focusAfterEviction = signal<string | null>(null);

  /**
   * Whether the section is open. Session-lived rather than persisted: the board draws
   * the chevron but asks for no memory, and the only place a preference could live
   * writes to `localStorage`, which this story deliberately stays out of.
   */
  protected readonly open = signal(true);

  /**
   * Which nodes are showing their conversations.
   *
   * One `Set` here rather than a signal per row, so collapsing the section and reopening
   * it does not silently reopen every node. Replaced, never mutated — an in-place edit
   * is the same reference and would perform no signal write at all.
   */
  private readonly _expanded = signal<ReadonlySet<string>>(new Set());

  constructor() {
    effect(() => {
      // Both tracked: the first is the write that removes the row, the second is how
      // this knows a request that removed nothing has finished.
      const held = this.projects.entityMap();
      const pending = this._actions.pendingIds();
      const id = untracked(this._focusAfterEviction);

      if (id === null) {
        return;
      }

      const project = held[id];
      if (project !== undefined && project.isFavorite) {
        // Still listed and settled — the request failed, or the guard refused it.
        // Leaving the id set would fire this against an unrelated later removal.
        if (!pending.has(id)) {
          this._focusAfterEviction.set(null);
        }

        return;
      }

      this._focusAfterEviction.set(null);
      // A node that is gone should not reopen from an earlier state if it comes back,
      // and the `Set` would otherwise grow for the whole session.
      this.setExpanded(id, false);
      afterNextRender(
        () => {
          const toggle = this._toggle();
          if (toggle !== undefined) {
            // The heading, which is where `reveal()` also sends focus.
            toggle.nativeElement.focus();

            return;
          }

          // That was the last favourite, so the whole section went with the row and
          // there is nothing of it left to focus. The host owns what comes next.
          this.lastFavoriteRemoved.emit();
        },
        { injector: this._injector },
      );
    });
  }

  /**
   * Unfavourites a project, which is what removes it from this section.
   *
   * The intent is recorded *before* the request goes out, so the effect above has
   * somewhere to send focus when the `<li>` holding the menu trigger is destroyed.
   */
  protected unfavorite(project: ProjectSummaryDto): void {
    this._focusAfterEviction.set(project.id);
    this._actions.toggleFavorite(project);
  }

  /** Opens the delete confirmation, on the same terms as {@link unfavorite}. */
  protected remove(project: ProjectSummaryDto): void {
    this._focusAfterEviction.set(project.id);
    this._actions.beginDelete(project);
  }

  /**
   * The last favourite was removed, so this section is gone and focus has nowhere local
   * to go. The sidebar answers by focusing its search field, which is the control the
   * section used to sit beneath.
   */
  readonly lastFavoriteRemoved = output<void>();

  /**
   * Opens the section and takes focus, for the collapsed strip's star.
   *
   * Public because the strip's control lives in the other template branch, and this
   * component does not exist until that branch is replaced by the expanded one.
   */
  reveal(): void {
    this.open.set(true);
    afterNextRender(() => this._toggle()?.nativeElement.focus(), { injector: this._injector });
  }

  protected isExpanded(id: string): boolean {
    return this._expanded().has(id);
  }

  protected setExpanded(id: string, expanded: boolean): void {
    const next = new Set(this._expanded());
    if (expanded) {
      next.add(id);
    } else {
      next.delete(id);
    }

    this._expanded.set(next);
  }
}
