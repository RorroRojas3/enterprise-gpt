import {
  DestroyRef,
  EnvironmentInjector,
  Injectable,
  afterNextRender,
  computed,
  createEnvironmentInjector,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { signalMethod } from '@ngrx/signals';
import { Events } from '@ngrx/signals/events';
import { PendingAttachmentsStore } from '@core/composer/pending-attachments-store';
import { ConversationActivityStore } from '@core/conversations/conversation-activity-store';
import { conversationEvents } from '@core/events/conversation-events';
import { injectSignedOut } from '@core/events/session-events';
import { UnloadPrompt } from '@core/navigation/unload-prompt';
import { UploadStore } from '@core/uploads/upload-store';
import { CHAT_SESSION } from './chat-session-token';
import { TurnStore } from './turn-store';

/** One conversation's turn and composer uploads, owned by an injector whose destruction ends both. */
export interface ChatSession {
  readonly injector: EnvironmentInjector;
  readonly turn: InstanceType<typeof TurnStore>;
  readonly uploads: InstanceType<typeof UploadStore>;
}

function sameMembers(a: ReadonlySet<string>, b: ReadonlySet<string>): boolean {
  return a.size === b.size && [...a].every((id) => b.has(id));
}

/**
 * Keeps each conversation's session alive past navigation, so a turn streams on while another is
 * read. A session is dropped once left idle with nothing unsent.
 */
@Injectable({ providedIn: 'root' })
export class ChatSessions {
  private readonly _root = inject(EnvironmentInjector);
  private readonly _attachments = inject(PendingAttachmentsStore);
  private readonly _activity = inject(ConversationActivityStore);
  private readonly _sessions = signal<readonly ChatSession[]>([]);
  private readonly _active = signal<ChatSession | null>(null);
  /** Deleted while on screen: dropped when left, draft or not. */
  private readonly _doomed = new WeakSet<ChatSession>();

  /** The session `Chat` shows; null while no chat screen is mounted. */
  readonly active = this._active.asReadonly();

  private readonly _inFlight = computed(() =>
    this._sessions().filter((session) => session.turn.inFlight()),
  );

  readonly running = computed(
    () =>
      new Set(
        this._inFlight()
          .map((session) => session.turn.boundConversationId())
          .filter((id): id is string => id !== null),
      ),
    { equal: sameMembers },
  );

  /** Unlike {@link running}, counts a first prompt whose conversation has no id yet. */
  readonly runningCount = computed(() => this._inFlight().length);

  /**
   * Shows a conversation's session, or the newest blank one for `/chat`. A `signalMethod`, so its
   * body runs untracked: building a session creates effects.
   */
  readonly activate = signalMethod<string | undefined>((conversationId) => {
    const id = conversationId ?? null;
    const previous = this._active();
    const existing = id === null ? this.blank() : this.find(id);

    if (existing !== undefined) {
      // A handoff only ever targets a brand-new conversation, which has no session yet.
      this._attachments.clear();
    }

    const next = existing ?? this.create(id);
    if (next !== previous) {
      this._active.set(next);
      this.dropLater(previous);
    }

    if (id !== null) {
      this._activity.markSeen(id);
    }
  });

  constructor() {
    // The root does not own the injectors it parents, so their streams would outlive it.
    inject(DestroyRef).onDestroy(() => {
      for (const session of this._sessions()) {
        session.injector.destroy();
      }
    });

    // A reload or closed tab ends every turn in flight: the stream is not resumable.
    inject(UnloadPrompt).register(computed(() => this.runningCount() > 0));

    this._activity.connect({
      running: this.running,
      runningCount: this.runningCount,
      viewing: computed(() => this._active()?.turn.boundConversationId() ?? null),
      stop: (conversationId) => this.stop(conversationId),
    });

    inject(Events)
      .on(conversationEvents.deleted)
      .pipe(takeUntilDestroyed())
      .subscribe(({ payload: id }) => {
        const session = this.find(id);
        if (session === undefined) {
          return;
        }

        // The mounted view still resolves through the active session's injector.
        if (session === this._active()) {
          this._doomed.add(session);
        } else {
          this.destroy(session);
        }
      });

    // The active session resets itself; the others hold the previous user's work.
    injectSignedOut()
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        for (const session of this._sessions()) {
          if (session !== this._active()) {
            this.destroy(session);
          }
        }
      });
  }

  /** `Chat` is leaving the screen. */
  deactivate(): void {
    const previous = this._active();
    this._active.set(null);
    this.dropLater(previous);
  }

  /** Stop from outside the conversation, in the composer's order: the turn, then its uploads. */
  stop(conversationId: string): void {
    const session = this.find(conversationId);
    if (session !== undefined) {
      session.turn.stop();
      session.uploads.cancelAll();
    }
  }

  private find(conversationId: string): ChatSession | undefined {
    return this._sessions().find(
      (session) => session.turn.boundConversationId() === conversationId,
    );
  }

  /** A first prompt still being created is not blank: it is about to have an id. */
  private blank(): ChatSession | undefined {
    return this._sessions()
      .filter((session) => session.turn.boundConversationId() === null && !session.turn.inFlight())
      .at(-1);
  }

  private create(conversationId: string | null): ChatSession {
    const injector: EnvironmentInjector = createEnvironmentInjector(
      [
        UploadStore,
        TurnStore,
        {
          provide: CHAT_SESSION,
          useValue: { isActive: () => this._active()?.injector === injector },
        },
      ],
      this._root,
      'ChatSession',
    );

    const uploads = injector.get(UploadStore);
    // Attaching states intent and Send is the instruction; `TurnStore` posts them at send time.
    uploads.deferUploadsUntilSend();
    if (conversationId !== null) {
      uploads.bindConversation(conversationId);
    }

    const session: ChatSession = { injector, turn: injector.get(TurnStore), uploads };
    this._sessions.update((sessions) => [...sessions, session]);
    session.turn.init(conversationId);

    return session;
  }

  /** After the next render, once the outlet has swapped the view off this session's injector. */
  private dropLater(session: ChatSession | null): void {
    if (session === null) {
      return;
    }

    afterNextRender(
      () => {
        if (session !== this._active() && this.isDisposable(session)) {
          this.destroy(session);
        }
      },
      { injector: this._root },
    );
  }

  private isDisposable(session: ChatSession): boolean {
    if (this._doomed.has(session)) {
      return true;
    }

    const { turn, uploads } = session;

    return (
      !turn.inFlight() &&
      turn.draft().trim() === '' &&
      turn.composerSeed() === null &&
      uploads.attachments().length === 0
    );
  }

  private destroy(session: ChatSession): void {
    this._sessions.update((sessions) => sessions.filter((held) => held !== session));
    session.injector.destroy();
  }
}
