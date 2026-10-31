import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Icon } from '@shared/icons/icon';
import { StatusDot } from './status-dot';

/** What a conversation is doing that its reader has not yet seen. */
export type ConversationActivity = 'running' | 'answer' | 'interrupted';

const ACTIVITY_LABELS = {
  running: 'Generating answer',
  answer: 'New answer',
  interrupted: 'Response interrupted',
} as const satisfies Record<ConversationActivity, string>;

/**
 * A conversation's activity as a spinner, dot or warning. Never interactive, so it can sit inside
 * a row's link and extend its accessible name; colour is never the only channel.
 */
@Component({
  selector: 'app-activity-indicator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, StatusDot],
  host: { class: 'd-inline-flex align-items-center justify-content-center flex-shrink-0' },
  templateUrl: './activity-indicator.html',
})
export class ActivityIndicator {
  readonly activity = input.required<ConversationActivity>();
  /** Off where the host control's `aria-label` already says it, which would otherwise repeat. */
  readonly labelled = input(true);

  protected readonly label = computed(() => ACTIVITY_LABELS[this.activity()]);
}
