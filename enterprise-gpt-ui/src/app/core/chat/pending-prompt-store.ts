import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

interface PendingPromptState {
  /** The conversation the prompt is for, or null when nothing is waiting. */
  readonly conversationId: string | null;
  readonly prompt: string;
}

/**
 * One prompt, handed from the screen that created a conversation to the screen that
 * streams it (US-906).
 *
 * The project detail screen creates a conversation inside the project and then
 * navigates to `/chat/{id}`, because the turn has to stream where the transcript is —
 * its second acceptance criterion, and the only way the answer is readable. That
 * navigation destroys the composer holding the text, so the text has to survive it
 * somewhere, and the router is not that somewhere: a prompt in a URL is a prompt in the
 * browser's history, in the address bar and in every referrer header the page sends.
 *
 * Root-scoped and single-shot. `TurnStore` claims it when the route binds a matching id
 * and clears it in the same call, so a refresh does not resend, and a prompt left over
 * from a navigation that never completed cannot attach itself to an unrelated
 * conversation opened later — the id has to match.
 */
export const PendingPromptStore = signalStore(
  { providedIn: 'root' },
  withState<PendingPromptState>({ conversationId: null, prompt: '' }),
  withMethods((store) => ({
    /**
     * Leaves a prompt for a conversation that is about to be opened.
     *
     * @param conversationId The conversation the server just created.
     * @param prompt The text the user typed.
     */
    hold(conversationId: string, prompt: string): void {
      patchState(store, { conversationId, prompt });
    },

    /**
     * Takes the prompt held for a conversation, if there is one.
     *
     * Clearing here rather than in a separate call is what makes it single-shot: there
     * is no window in which a second reader could take the same prompt.
     *
     * @param conversationId The conversation being opened.
     * @returns The prompt, or null when none was held for this conversation.
     */
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
