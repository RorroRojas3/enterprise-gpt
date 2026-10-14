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
import { ProjectSummaryDto } from '@shared/models/api/project.model';
import { PROJECTS_ROUTE } from '@core/navigation/route-paths';
import { ProjectActionsStore } from '@state/project-actions-store';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { MenuItem } from '@shared/directives/menu-item';
import { MenuSeparator } from '@shared/directives/menu-separator';
import { SidebarProjectConversations } from './project-conversations';

/**
 * Link, disclosure and kebab sit side by side, as interactive content inside an `<a>` is invalid.
 * Expansion is owned by the parent, so collapsing the section does not reopen every node.
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
  host: { class: 'd-block' },
})
export class SidebarProjectRow {
  protected readonly actions = inject(ProjectActionsStore);

  readonly project = input.required<ProjectSummaryDto>();

  /** Whether this node's conversations are showing. Owned by `SidebarFavorites`. */
  readonly expanded = model.required<boolean>();

  /** Announced, not performed: the parent must be ready to move focus when this row goes. */
  readonly unfavorited = output<void>();

  /** Delete was chosen. Announced for the same reason as {@link unfavorited}. */
  readonly deleted = output<void>();

  /** This project's own request is in flight — a rename, a delete, or the star. */
  protected readonly pending = computed(() => this.actions.isRowPending(this.project().id));

  protected readonly link = computed(() => [PROJECTS_ROUTE, this.project().id]);

  /** Bound to `aria-controls` only while expanded: an absent id is invalid, not harmless. */
  protected readonly panelId = computed(() => `sidebar-project-${this.project().id}`);

  /** Spells out no open/closed state, which `aria-expanded` already announces. */
  protected readonly disclosureLabel = computed(() => `Conversations in ${this.project().name}`);

  protected toggle(): void {
    this.expanded.set(!this.expanded());
  }
}
