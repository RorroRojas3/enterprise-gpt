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
 * Carries a new conversation's files across the navigation to `/chat/{id}`, which destroys the
 * composer; uploading first would leave the user watching the project screen during ingestion.
 * Single-shot and id-matched like {@link PendingPromptStore}. Unlike `UploadStore`, the handles
 * live in state so `withResetOnSignOut` can release the previous user's bytes.
 */
export const PendingAttachmentsStore = signalStore(
  { providedIn: 'root' },
  withState<PendingAttachmentsState>({ conversationId: null, files: NONE }),
  withMethods((store) => ({
    hold(conversationId: string, files: readonly File[]): void {
      patchState(store, { conversationId, files });
    },

    /** Clears in the same call, so the files can be claimed only once. */
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
