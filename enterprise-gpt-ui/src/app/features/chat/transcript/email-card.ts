import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { EmailDraft } from '@domain/email/email-draft';
import { Icon } from '@shared/icon/icon';
import { EmailOpenMenu } from './email-open-menu';

/**
 * An email the assistant composed, rendered as a form rather than prose. The
 * recipient is always shown: answer text is shaped by whatever the turn read, so
 * the address is not necessarily one the user chose.
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
