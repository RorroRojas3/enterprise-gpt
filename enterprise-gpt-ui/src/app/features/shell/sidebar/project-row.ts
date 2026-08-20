import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  model,
  output,
} from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ProjectSummaryDto } from '@domain/api/project';
import { PROJECTS_ROUTE } from '@core/auth/auth-routes';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { Icon } from '@shared/icon/icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { SidebarProjectConversations } from './project-conversations';

/**
 * One favourite project in the sidebar, and its conversations when expanded (US-910).
 *
 * Three controls sit side by side rather than nested, for the reason
 * `ConversationRow` records: an interactive element inside an `<a>` is invalid HTML and
 * splits the accessible name. The anchor opens the project, the disclosure button opens
 * the node, and the kebab opens the menu — each with its own name.
 *
 * The kebab's three actions all belong to `ProjectActionsStore`, whose dialogs are
 * already mounted once in the shell that hosts this sidebar, so the row only says which
 * project is meant. Unfavourite is not optimistic; see that store's `toggleFavorite`.
 *
 * Expansion is owned by the parent rather than held here, so collapsing the whole
 * section and re-expanding it does not silently reopen every node — and so the sidebar
 * can keep one `Set` instead of one signal per row.
 */
@Component({
  selector: 'app-sidebar-project-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    Icon,
    Menu,
    MenuItem,
    MenuSeparator,
    RouterLink,
    RouterLinkActive,
    SidebarProjectConversations,
  ],
  templateUrl: './project-row.html',
  styleUrl: './project-row.scss',
})
export class SidebarProjectRow {
  protected readonly actions = inject(ProjectActionsStore);

  readonly project = input.required<ProjectSummaryDto>();

  /** Whether this node's conversations are showing. Owned by `SidebarFavorites`. */
  readonly expanded = model.required<boolean>();

  /**
   * Unfavourite was chosen. Announced rather than performed, because the row it was
   * invoked from is what the action removes: the parent has to know before the server
   * confirms, so focus has somewhere to go when this `<li>` is destroyed.
   */
  readonly unfavorited = output<void>();

  /** Delete was chosen. Announced for the same reason as {@link unfavorited}. */
  readonly deleted = output<void>();

  /** This project's own request is in flight — a rename, a delete, or the star. */
  protected readonly pending = computed(() => this.actions.isRowPending(this.project().id));

  protected readonly link = computed(() => [PROJECTS_ROUTE, this.project().id]);

  /**
   * `aria-controls` only while the panel it names exists. A collapsed node has no
   * nested list in the DOM, and pointing at an absent id is an invalid attribute value
   * rather than a harmless one.
   */
  protected readonly panelId = computed(() => `sidebar-project-${this.project().id}`);

  /**
   * Names the disclosure without repeating the project name the row already shows —
   * `aria-expanded` carries the state, so the label must not also spell it out, or the
   * control announces its condition twice.
   */
  protected readonly disclosureLabel = computed(() => `Conversations in ${this.project().name}`);

  protected toggle(): void {
    this.expanded.set(!this.expanded());
  }
}
