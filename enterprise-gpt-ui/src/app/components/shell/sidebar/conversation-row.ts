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
import { ConversationDto } from '@shared/models/api/conversation.model';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { ConversationActionsStore } from '@state/conversation-actions-store';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { ProjectPicker } from '@shared/components/project-picker/project-picker';
import { MenuItem } from '@shared/directives/menu-item';
import { MenuSeparator } from '@shared/directives/menu-separator';

/**
 * Selection comes from the router, not an input. The kebab's dialogs are mounted once in the
 * shell; the row only names the conversation.
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
