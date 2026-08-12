import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { BrandLogo } from '@shared/brand-logo/brand-logo';

/**
 * Frame `1a`: the centred wordmark and prompt a new conversation opens on.
 *
 * "New conversation" navigates here and **creates nothing** — no `POST api/conversations`
 * is issued until the first prompt is sent (US-401), which is what keeps an abandoned
 * idea out of the sidebar.
 *
 * The board also draws four suggested prompt chips. They are deferred to US-401 with
 * the composer, because a chip's only behaviour is to seed the composer's text: shipped
 * now it would be a control that visibly does nothing.
 *
 * No focus is moved on arrival. This is the application's landing screen rather than a
 * state transition, so taking focus here would jump a keyboard user past the shell's
 * skip link on every cold start.
 */
@Component({
  selector: 'app-chat-empty-state',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandLogo],
  templateUrl: './chat-empty-state.html',
  styleUrl: './chat-empty-state.scss',
})
export class ChatEmptyState {
  /**
   * Whether the prompt is this screen's `<h1>`.
   *
   * False once a conversation is open, because the 52px header's conversation name is
   * the page heading then and a second `<h1>` would compete with it.
   */
  readonly asPageHeading = input<boolean>(true);
}
