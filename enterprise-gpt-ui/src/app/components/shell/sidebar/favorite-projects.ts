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
import { ProjectSummaryDto } from '@shared/models/api/project.model';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { ProjectActionsStore } from '@state/project-actions-store';
import { Icon } from '@shared/components/icon/icon';
import { SidebarProjectRow } from './project-row';

/**
 * Server favourites, not device pins, read from the bootstrap-loaded {@link ProjectLookupStore}.
 * No request or error branch of its own: the picker reports that store's failures.
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
   * Unfavourite and Delete destroy the row whose trigger holds focus, and `Menu`'s own focus
   * rescue with it, so focus is moved from here.
   */
  private readonly _focusAfterEviction = signal<string | null>(null);

  /** Session-lived, not persisted: this section stays out of `localStorage`. */
  protected readonly open = signal(true);

  /** Which nodes are open; kept here so collapsing the section does not reopen every node. */
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
        // Still listed and settled: the request failed or was refused. A stale id would fire
        // this against an unrelated later removal.
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

          // The last favourite took the whole section with it; the host places focus.
          this.lastFavoriteRemoved.emit();
        },
        { injector: this._injector },
      );
    });
  }

  /** Records the intent before the request, so the effect can move focus when the row goes. */
  protected unfavorite(project: ProjectSummaryDto): void {
    this._focusAfterEviction.set(project.id);
    this._actions.toggleFavorite(project);
  }

  /** Opens the delete confirmation, on the same terms as {@link unfavorite}. */
  protected remove(project: ProjectSummaryDto): void {
    this._focusAfterEviction.set(project.id);
    this._actions.beginDelete(project);
  }

  /** The section left with its last favourite, so focus has nowhere local to go. */
  readonly lastFavoriteRemoved = output<void>();

  /** Opens and focuses the section, for the collapsed strip's star in the other branch. */
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
