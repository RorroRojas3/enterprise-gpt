import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AssistantActivity } from '@domain/stream/andes/assistant-ui.contract';
import { KindBadge } from '@shared/badge/kind-badge/kind-badge';
import { Icon } from '@shared/icon/icon';

/**
 * One activity of the assistant's turn, as frame `1f` draws it (US-501).
 *
 * `displayName` is the label and the kind is a **separate** badge — the
 * contract keeps them apart so no card ever reads "Calling Jira Cloud MCP".
 * `source` renders verbatim: the "Atlassian · host" composition is the
 * server's, arriving as one string. No chevron and no expansion yet — that
 * affordance arrives with US-504's usage strip.
 */
@Component({
  selector: 'app-activity-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, KindBadge],
  templateUrl: './activity-card.html',
  styleUrl: './activity-card.scss',
})
export class ActivityCard {
  readonly activity = input.required<AssistantActivity>();

  /** Frame `1f` writes durations as `3.1s` in JetBrains Mono; absent until completion. */
  protected readonly duration = computed(() => {
    const seconds = this.activity().durationSeconds;

    return seconds === undefined ? null : `${seconds.toFixed(1)}s`;
  });

  protected readonly stateLabel = computed(() => STATE_LABELS[this.activity().state]);
}

const STATE_LABELS: Record<AssistantActivity['state'], string> = {
  Running: 'Running',
  Completed: 'Completed',
  Failed: 'Failed',
};
