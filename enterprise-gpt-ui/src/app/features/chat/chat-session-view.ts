import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Composer } from '@core/composer/composer';
import { canRetry } from '@core/errors/error-message';
import { ErrorPanel } from '@core/errors/error-panel';
import { provideSharedComposerUploads } from '@core/uploads/composer-uploads-token';
import { Icon } from '@shared/icons/icon';
import { provideChatComposerHost } from './chat-composer-host';
import { ChatEmptyState } from './chat-empty-state';
import { ConversationStore } from './conversation-store';
import { TranscriptPinning } from './transcript-pinning';
import { Transcript } from './transcript/transcript';
import { TurnStore } from './turn-store';

/** The transcript and composer of one conversation's session, below `Chat`'s header. */
@Component({
  selector: 'app-chat-session-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatEmptyState, Composer, ErrorPanel, Icon, Transcript, TranscriptPinning],
  // Element providers, so they resolve the session's `TurnStore` and `UploadStore`.
  providers: [provideSharedComposerUploads(), provideChatComposerHost()],
  templateUrl: './chat-session-view.html',
  styleUrl: './chat-session-view.scss',
  host: { class: 'd-flex flex-column flex-fill min-h-0' },
})
export class ChatSessionView {
  protected readonly conversation = inject(ConversationStore);
  protected readonly turn = inject(TurnStore);

  /** A 404 (missing or someone else's) never succeeds on retry, so the panel offers none. */
  protected readonly canRetry = canRetry;
}
