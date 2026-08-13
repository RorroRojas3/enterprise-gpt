import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { Icon } from '@shared/icon/icon';
import { IconName } from '@shared/icon/icon-names';

/**
 * Frame `1a`: the centred wordmark, prompt, and suggested prompt chips a new
 * conversation opens on.
 *
 * "New conversation" navigates here and **creates nothing** — no `POST api/conversations`
 * is issued until the first prompt is sent (US-401), which is what keeps an abandoned
 * idea out of the sidebar.
 *
 * A chip's only behaviour is to seed the composer's text (deferred out of US-303 into
 * US-401, where the composer can finally send it): the component stays presentational
 * and `suggested` carries the label out to whoever owns the composer's seed.
 *
 * No focus is moved on arrival. This is the application's landing screen rather than a
 * state transition, so taking focus here would jump a keyboard user past the shell's
 * skip link on every cold start.
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
   * Whether the prompt is this screen's `<h1>`.
   *
   * False once a conversation is open, because the 52px header's conversation name is
   * the page heading then and a second `<h1>` would compete with it. The chips follow
   * it: they belong to the landing screen alone (frame `1a`).
   */
  readonly asPageHeading = input<boolean>(true);

  /** A suggested prompt was chosen; the payload is the text to seed the composer with. */
  readonly suggested = output<string>();

  /** Frame 1a's four suggestions, verbatim. */
  protected readonly chips: readonly { icon: IconName; label: string }[] = [
    { icon: 'bi-file-text', label: 'Summarize a document' },
    { icon: 'bi-pencil-square', label: 'Draft a status update' },
    { icon: 'bi-kanban', label: 'Find open Jira blockers' },
    { icon: 'bi-code-slash', label: 'Explain a code change' },
  ];
}
