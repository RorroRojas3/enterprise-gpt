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
import { ConversationDto } from '@core/api/conversation.model';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { ProjectPicker } from '@core/projects/project-picker';
import { Icon } from '@shared/icons/icon';
import { Menu } from '@shared/menu/menu';
import { MenuItem } from '@shared/menu/menu-item';
import { MenuSeparator } from '@shared/menu/menu-separator';

/**
 * Selection comes from the router, not an input. The kebab's dialogs are mounted once in the
 * shell; the row only names the conversation.
 */
@Component({
  selector: 'app-conversation-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, Menu, MenuItem, MenuSeparator, ProjectPicker, RouterLink, RouterLinkActive],
  templateUrl: './conversation-row.html',
  host: { class: 'd-block' },
})
export class ConversationRow {
  protected readonly actions = inject(ConversationActionsStore);
  private readonly _list = inject(ConversationListStore);
  private readonly menu = viewChild.required(Menu);

  readonly conversation = input.required<ConversationDto>();

  /** The project picker, opened from the kebab and anchored where the kebab was. */
  protected readonly pickerOpen = signal(false);
  protected readonly pickerAnchor = signal<HTMLElement | null>(null);

  /** This row's own action is in flight — e.g. a rename whose dialog was force-closed. */
  protected readonly pending = computed(() => this._list.pendingIds().has(this.conversation().id));

  protected readonly link = computed(() => ['/chat', this.conversation().id]);

  /** Exact, so `/chat/a` does not also light up while `/chat/b` is open. */
  protected readonly exact = { exact: true } as const;

  /**
   * Captures the anchor at the gesture: the menu closes on this click, and a template binding
   * would read the trigger before the menu's view exists.
   */
  protected openPicker(): void {
    if (this.pending()) {
      return;
    }

    this.pickerAnchor.set(this.menu().triggerElement());
    this.pickerOpen.set(true);
  }
}
