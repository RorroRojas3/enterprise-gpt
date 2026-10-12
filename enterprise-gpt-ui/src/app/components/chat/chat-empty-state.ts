import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { IconName } from '@shared/utils/icons/icon-names';
import { BrandLogo } from '@shared/components/brand-logo/brand-logo';
import { Icon } from '@shared/components/icon/icon';

/**
 * Creates nothing: `POST api/conversations` waits for the first prompt, so abandoned ideas stay
 * out of the sidebar. Takes no focus on arrival, which would skip past the shell's skip link.
 */
@Component({
  selector: 'app-chat-empty-state',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandLogo, Icon],
  templateUrl: './chat-empty-state.html',
  styleUrl: './chat-empty-state.scss',
})
export class ChatEmptyState {
  /**
   * False once a conversation is open, whose header name is then the `<h1>`. Also gates the
   * chips, which belong to the landing screen alone.
   */
  readonly asPageHeading = input<boolean>(true);

  /** A suggested prompt was chosen; the payload is the text to seed the composer with. */
  readonly suggested = output<string>();

  /** Each seeds the composer verbatim, so a chip must name something the platform does. */
  protected readonly chips: readonly { icon: IconName; label: string }[] = [
    { icon: 'bi-file-text', label: 'Summarize a document' },
    { icon: 'bi-pencil-square', label: 'Draft a status update' },
    { icon: 'bi-envelope', label: 'Draft an email' },
    { icon: 'bi-file-earmark-plus', label: 'Generate a document' },
  ];
}
