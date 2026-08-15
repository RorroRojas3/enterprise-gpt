import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ConversationDto } from '@domain/api/conversation';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { Icon } from '@shared/icon/icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { ProjectPicker } from '@shared/projects/project-picker/project-picker';

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
  imports: [Icon, Menu, MenuItem, MenuSeparator, ProjectPicker, RouterLink, RouterLinkActive],
  templateUrl: './conversation-row.html',
  styleUrl: './conversation-row.scss',
})
export class ConversationRow {
  protected readonly actions = inject(ConversationActionsStore);
  private readonly _list = inject(ConversationListStore);
  private readonly menu = viewChild.required(Menu);

  readonly conversation = input.required<ConversationDto>();

  /** Frame `2e`'s panel, opened from the kebab and anchored where the kebab was. */
  protected readonly pickerOpen = signal(false);
  protected readonly pickerAnchor = signal<HTMLElement | null>(null);

  /** This row's own action is in flight — e.g. a rename whose dialog was force-closed. */
  protected readonly pending = computed(() => this._list.pendingIds().has(this.conversation().id));

  protected readonly link = computed(() => ['/chat', this.conversation().id]);

  /** Exact, so `/chat/a` does not also light up while `/chat/b` is open. */
  protected readonly exact = { exact: true } as const;

  /**
   * Opens the picker where the kebab is.
   *
   * The anchor is captured at the gesture rather than bound in the template: the menu
   * closes on this same click and returns focus to its trigger, and reading the trigger
   * through a binding would evaluate it on the first change-detection pass, before the
   * menu's view exists.
   */
  protected openPicker(): void {
    if (this.pending()) {
      return;
    }

    this.pickerAnchor.set(this.menu().triggerElement());
    this.pickerOpen.set(true);
  }
}
