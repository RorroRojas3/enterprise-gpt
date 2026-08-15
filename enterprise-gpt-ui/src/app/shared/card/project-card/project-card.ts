import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Icon } from '@shared/icon/icon';

let nextId = 0;

/**
 * A project tile — frames `4c` and `4d`.
 *
 * The title is a real `<a routerLink>` rather than a clickable card, so the whole tile
 * is not one giant unlabelled button and the link is reachable, focusable and
 * middle-clickable. The row menu projects into `[cardMenu]`.
 */
@Component({
  selector: 'app-project-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, Icon],
  templateUrl: './project-card.html',
  styleUrl: './project-card.scss',
})
export class ProjectCard {
  readonly name = input.required<string>();
  readonly description = input<string>('');

  /** Already formatted — "2h ago". Relative-time policy is the caller's. */
  readonly updatedLabel = input.required<string>();

  readonly link = input.required<readonly unknown[] | string>();
  readonly favorite = input<boolean>(false);

  /**
   * Whether to render the star at all.
   *
   * False until US-909 puts a favourite flag on `ProjectSummaryDto`: the board draws
   * the star, but nothing on the wire can persist a press, and a control that silently
   * forgets is worse than one that is not there — the same call US-308 made about this
   * star's conversation counterpart.
   */
  readonly showFavorite = input<boolean>(true);

  /** From `withPendingIds`: this row alone is busy, the rest stay interactive. */
  readonly pending = input<boolean>(false);

  readonly favoriteToggled = output<void>();

  protected readonly headingId = `project-card-${nextId++}`;
  protected readonly favoriteLabel = computed(() =>
    this.favorite() ? `Unfavourite ${this.name()}` : `Favourite ${this.name()}`,
  );
}
