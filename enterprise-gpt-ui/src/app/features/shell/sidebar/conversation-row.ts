import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ConversationDto } from '@domain/api/conversation';
import { Icon } from '@shared/icon/icon';

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
 */
@Component({
  selector: 'app-conversation-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, RouterLink, RouterLinkActive],
  templateUrl: './conversation-row.html',
  styleUrl: './conversation-row.scss',
})
export class ConversationRow {
  readonly conversation = input.required<ConversationDto>();

  protected readonly link = computed(() => ['/chat', this.conversation().id]);

  /** Exact, so `/chat/a` does not also light up while `/chat/b` is open. */
  protected readonly exact = { exact: true } as const;
}
