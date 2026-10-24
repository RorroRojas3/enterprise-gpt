import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';

interface PendingPromptState {
  /** The conversation the prompt is for, or null when nothing is waiting. */
  readonly conversationId: string | null;
  readonly prompt: string;
}

/**
 * Carries a new conversation's prompt across the navigation to `/chat/{id}`, which destroys the
 * composer. Not the URL: that would leak the prompt into history and referrer headers.
 * Single-shot and id-matched, so a refresh never resends and a stale prompt never attaches
 * to another conversation.
 */
export const PendingPromptStore = signalStore(
  { providedIn: 'root' },
  withState<PendingPromptState>({ conversationId: null, prompt: '' }),
  withMethods((store) => ({
    hold(conversationId: string, prompt: string): void {
      patchState(store, { conversationId, prompt });
    },

    /** Clears in the same call, so no second reader can take the same prompt. */
    claim(conversationId: string): string | null {
      if (store.conversationId() !== conversationId) {
        return null;
      }

      const prompt = store.prompt();
      patchState(store, { conversationId: null, prompt: '' });

      return prompt;
    },

    /** Drops anything held, for a navigation that was abandoned. */
    clear(): void {
      patchState(store, { conversationId: null, prompt: '' });
    },
  })),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
