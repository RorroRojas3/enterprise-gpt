import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { EmailDraft } from '@shared/utils/email/email-draft';
import { Icon } from '@shared/components/icon/icon';
import { EmailOpenMenu } from './email-open-menu';

/**
 * The recipient is always shown: the answer is shaped by whatever the turn read, so the address
 * is not necessarily one the user chose.
 */
@Component({
  selector: 'app-email-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [EmailOpenMenu, Icon],
  templateUrl: './email-card.html',
  styleUrl: './email-card.scss',
})
export class EmailCard {
  readonly draft = input.required<EmailDraft>();

  protected readonly to = computed(() => this.draft().to.join(', '));
  protected readonly cc = computed(() => this.draft().cc.join(', '));
}
