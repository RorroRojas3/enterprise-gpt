import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ConversationDto } from '@domain/api/conversation';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { Icon } from '@shared/icon/icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';

/**
 * One conversation in the sidebar list (frame `3a`).
 *
 * Its own component rather than markup inline in the sidebar because US-304, US-305
 * and US-306 hang the row kebab, the favourite toggle and per-row pending state off
 * exactly this element.
 *
 * Selection comes from `routerLinkActive` with `exact: true` rather than from an
 * input the sidebar computes: the router already knows which conversation is open,
 * and `ariaCurrentWhenActive` makes the visual state and the announced one the same
 * fact rather than two that can disagree.
 *
 * The kebab's actions go through `ConversationActionsStore`, whose dialogs are
 * mounted once in the shell — the row only says which conversation is meant.
 */
@Component({
  selector: 'app-conversation-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, Menu, MenuItem, MenuSeparator, RouterLink, RouterLinkActive],
  templateUrl: './conversation-row.html',
  styleUrl: './conversation-row.scss',
})
export class ConversationRow {
  protected readonly actions = inject(ConversationActionsStore);
  private readonly _list = inject(ConversationListStore);

  readonly conversation = input.required<ConversationDto>();

  /** This row's own action is in flight — e.g. a rename whose dialog was force-closed. */
  protected readonly pending = computed(() =>
    this._list.pendingIds().has(this.conversation().id),
  );

  protected readonly link = computed(() => ['/chat', this.conversation().id]);

  /** Exact, so `/chat/a` does not also light up while `/chat/b` is open. */
  protected readonly exact = { exact: true } as const;
}
