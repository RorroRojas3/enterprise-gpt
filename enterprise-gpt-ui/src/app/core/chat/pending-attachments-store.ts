import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

interface PendingAttachmentsState {
  /** The conversation the files are for, or null when nothing is waiting. */
  readonly conversationId: string | null;
  readonly files: readonly File[];
}

/** Shared so a miss returns one frozen reference rather than a new array each time. */
const NONE: readonly File[] = Object.freeze([]);

/**
 * Files handed from the screen that created a conversation to the screen that uploads
 * them, alongside the prompt {@link PendingPromptStore} carries.
 *
 * The project detail screen creates a conversation and navigates to `/chat/{id}`, which
 * destroys the composer holding the chips. Uploading before navigating was the
 * alternative and is worse: ingestion runs for seconds to minutes, and the user would
 * watch a project screen for all of it.
 *
 * Root-scoped and single-shot, id-matched exactly as the prompt store is, so a refresh
 * does not re-attach and files left over from an abandoned navigation cannot fasten
 * themselves onto an unrelated conversation opened later.
 *
 * The handles live in **state**, unlike `UploadStore`'s — the opposite choice, for the
 * opposite reason. This store is root-scoped and never destroyed, so `withResetOnSignOut`
 * is the only thing that can release the previous user's bytes; and these are written
 * once and read once, never rendered, so none of the churn arguments apply.
 */
export const PendingAttachmentsStore = signalStore(
  { providedIn: 'root' },
  withState<PendingAttachmentsState>({ conversationId: null, files: NONE }),
  withMethods((store) => ({
    /**
     * Leaves files for a conversation that is about to be opened.
     *
     * @param conversationId The conversation the server just created.
     * @param files The bytes behind the chips the composer held.
     */
    hold(conversationId: string, files: readonly File[]): void {
      patchState(store, { conversationId, files });
    },

    /**
     * Takes the files held for a conversation, if there are any.
     *
     * Clearing in the same call is what makes it single-shot.
     *
     * @param conversationId The conversation being opened.
     * @returns The files, or an empty array when none were held for this conversation.
     */
    claim(conversationId: string): readonly File[] {
      if (store.conversationId() !== conversationId) {
        return NONE;
      }

      const files = store.files();
      patchState(store, { conversationId: null, files: NONE });

      return files;
    },

    /** Drops anything held, for a navigation that was abandoned. */
    clear(): void {
      patchState(store, { conversationId: null, files: NONE });
    },
  })),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
