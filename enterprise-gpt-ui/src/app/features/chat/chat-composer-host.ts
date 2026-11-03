import { Injectable, Provider, computed, inject } from '@angular/core';
import { ConversationDto } from '@core/api/conversation.model';
import {
  ComposerExportTarget,
  ComposerHost,
  ComposerProjectTarget,
  provideComposerHost,
} from '@core/composer/composer-host-token';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { ConversationStore } from './conversation-store';
import { TurnStore } from './turn-store';

/**
 * List copy first, as `moveRow` patches it optimistically; the detail covers rows the sidebar never
 * held. Null while neither is in hand, since `toUpdateBody` echoes `projectId`.
 */
export function conversationOf(
  id: string | null,
  list: Readonly<Record<string, ConversationDto>>,
  detail: ConversationDto | null,
): ConversationDto | null {
  if (id === null) {
    return null;
  }

  return list[id] ?? (detail?.id === id ? detail : null);
}

/** A download needs a transcript with entries, not merely pending history or a notice. */
export function exportTargetOf(
  conversation: ConversationDto | null,
  hasEntries: boolean,
): ComposerExportTarget | null {
  return conversation === null || !hasEntries
    ? null
    : { id: conversation.id, name: conversation.name };
}

/**
 * The chat screen's composer host: the session's `TurnStore` runs the prompt, and the targets come
 * from the screen's own `ConversationStore`, which a session outlives.
 */
@Injectable()
export class ChatComposerHost implements ComposerHost {
  private readonly turn = inject(TurnStore);
  private readonly detail = inject(ConversationStore);
  private readonly list = inject(ConversationListStore);

  private readonly conversation = computed(() =>
    conversationOf(
      this.turn.boundConversationId(),
      this.list.entityMap(),
      this.detail.conversation(),
    ),
  );

  readonly composerSeed = this.turn.composerSeed;
  readonly draft = this.turn.draft;
  readonly inFlight = this.turn.inFlight;
  readonly turnError = this.turn.turnError;
  readonly consumeComposerSeed = this.turn.consumeComposerSeed;
  readonly setDraft = this.turn.setDraft;
  readonly send = this.turn.send;
  readonly stop = this.turn.stop;

  readonly projectTarget = computed<ComposerProjectTarget | null>(() => {
    const conversation = this.conversation();

    return conversation === null ? null : { kind: 'conversation', conversation };
  });

  readonly exportTarget = computed(() =>
    exportTargetOf(this.conversation(), this.turn.entries().length > 0),
  );
}

export function provideChatComposerHost(): Provider[] {
  return [ChatComposerHost, provideComposerHost(ChatComposerHost)];
}
