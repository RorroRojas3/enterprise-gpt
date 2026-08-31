import { computed, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  PartialStateUpdater,
  patchState,
  signalMethod,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { Observable, Subject, Subscription, of, take, takeUntil } from 'rxjs';
import { JOB_STATE, UploadTarget } from '@domain/api/document';
import { subStatusOf } from '@domain/documents/upload-schedule';
import { formatBytes } from '@domain/format/bytes';
import { AppError } from '@core/errors/app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { Attachment, AttachmentRemoveAction, AttachmentState } from '@domain/documents/attachment';
import { DocumentDownloadStore } from './document-download-store';
import { DocumentUploadClient } from './document-upload-client';
import { UploadCancelClient } from './upload-cancel-client';
import { describeUploadRejection } from './upload-rejection';
import { UploadStatusClient } from './upload-status-client';
import { SupportedExtensionsStore, extensionOf } from './supported-extensions-store';

/**
 * One attached file, as the store tracks it.
 *
 * `attachment` is the presentational slice the kit renders; everything beside it is
 * plumbing the chip has no business knowing about. Immutable, and replaced rather than
 * mutated on every transition — `provideCheckNoChangesConfig({ exhaustive: true })`
 * turns an in-place edit into a failing test rather than a chip that silently stops
 * updating, which is the failure US-801's last acceptance criterion names.
 */
export interface UploadItem {
  readonly attachment: Attachment;
  /** Present once the server has queued the job; the key the status route polls. */
  readonly jobId: string | null;
  /** Present once the job has succeeded; what US-804 downloads without a lookup. */
  readonly documentId: string | null;
  /**
   * Whether this file has been handed to the transport yet.
   *
   * Distinct from its state: a deferred file sits in `uploading` with nothing in flight,
   * and treating that as work in progress would hold Send against a request nobody made.
   */
  readonly started: boolean;
}

/** One chip, with every decision the template would otherwise have to ask for. */
export interface AttachmentRow extends Attachment {
  readonly removeAction: AttachmentRemoveAction;
  /**
   * Whether a request is genuinely on its way for this file.
   *
   * Not the same as a non-terminal state: a file attached before its parent exists sits
   * in `uploading` with nothing in flight, and is the user's to remove. Only a busy row
   * has something for a Stop control to call off.
   */
  readonly busy: boolean;
  readonly downloadable: boolean;
  readonly downloading: boolean;
}

interface UploadState {
  /** The conversation or project these files belong to. Null defers every upload. */
  readonly target: UploadTarget | null;
  readonly items: readonly UploadItem[];
}

const initialState: UploadState = { target: null, items: [] };

/**
 * How many files are in flight at once, counting **transfer and ingest together**.
 *
 * Three, not "all of them": the picker takes `multiple` and a drop can carry a
 * folder's worth, while an HTTP/1.1 origin allows six connections in total — which the
 * SSE turn also draws on. The budget deliberately spans both phases rather than
 * freeing a slot at the 202: ingestion is the *longer* phase, so releasing early would
 * cap the transfers at three and then let fifty poll loops accumulate behind them,
 * which is the same saturation the cap exists to prevent.
 *
 * A cancelled file is the one exception: its slot is freed the moment the cancel is sent,
 * without waiting for the server to confirm, because work the user has already retracted
 * must not stall chips they can still see.
 */
const MAX_CONCURRENT_UPLOADS = 3;

/**
 * The files attached to the composer, and their journey to being usable (EP-8).
 *
 * **Component-scoped, provided by `Chat` and by the project files panel.** Root scope
 * would be wrong twice over: the chips belong to one draft on one screen, and US-905's
 * project files panel runs a second, independently-targeted instance alongside the
 * chat one. It lives in `core/` rather than beside the chat feature for the same
 * reason — `features/projects` may not import `features/chat`.
 *
 * Three decisions shape it.
 *
 * **Send is what posts a composer's files, and then waits for them.** A document is only
 * reachable by the model once ingestion has written its chunks, and `document_search` is
 * attached to a turn based on what the conversation already holds — so a file posted at
 * the same moment the stream opens is invisible to the very turn that asked about it.
 * `TurnStore` therefore calls {@link startPending} before gating the stream on
 * {@link settled$}. A file manager keeps the opposite default, where dropping a file is
 * itself the instruction; see {@link deferUploadsUntilSend}.
 *
 * **The dependency runs one way: `TurnStore` injects this store, never the reverse.**
 * This store learns its target by being told, through {@link bindConversation}, from
 * both the create path and the route. Injecting `TurnStore` back for the id would be a
 * DI cycle.
 *
 * **`File` handles and subscriptions live in `withProps`, not in state.** A `Map` in
 * state is either mutated — no signal write, so nothing re-renders — or replaced, which
 * re-renders every chip on each byte of progress. Neither is what a file handle
 * deserves; `ToastStore` holds its timers the same way and for the same reason.
 */
export const UploadStore = signalStore(
  withState(initialState),
  withProps(() => ({
    _client: inject(DocumentUploadClient),
    _status: inject(UploadStatusClient),
    _downloads: inject(DocumentDownloadStore),
    _cancels: inject(UploadCancelClient),
    _extensions: inject(SupportedExtensionsStore),
    _toasts: inject(ToastStore),
    _signedOut$: injectSignedOut(),
    /** The bytes themselves, kept for the deferred queue and for Retry. */
    _files: new Map<string, File>(),
    _subscriptions: new Map<string, Subscription>(),
    /** Files transferring right now, against {@link MAX_CONCURRENT_UPLOADS}. */
    _active: new Set<string>(),
    /** Files waiting for a slot. Insertion order is attach order. */
    _waiting: new Set<string>(),
    /** The failure toast each file currently owns, so it can be withdrawn with it. */
    _fileToasts: new Map<string, string>(),
    _sequence: { value: 0 },
    /** Fires whenever the last unsettled item settles. See {@link settled$}. */
    _settled$: new Subject<void>(),
    /**
     * Whether attaching a file is enough to upload it.
     *
     * True for a file manager, where dropping a file *is* the instruction. False for a
     * composer, where the instruction is Send — see {@link deferUploadsUntilSend}.
     */
    _startsOnAttach: { value: true },
  })),
  withComputed((store) => ({
    /**
     * One row per chip, with every per-row decision already made.
     *
     * Projected here rather than answered by methods the template calls: a
     * `removeActionFor(id)` binding would run an `Array.find` per chip on every
     * change-detection pass, and a progress event triggers one — O(n²) on a surface
     * whose whole point is to update continuously.
     */
    attachments: computed<readonly AttachmentRow[]>(() =>
      store.items().map((item) => ({
        ...item.attachment,
        // Once the document exists it is the conversation's, and no API detaches it,
        // so the control can only hide the chip from here on.
        removeAction: item.documentId === null ? 'cancel' : 'dismiss',
        busy: isBusy(item),
        downloadable: item.documentId !== null,
        downloading: item.documentId !== null && store._downloads.isRowPending(item.documentId),
      })),
    ),
    hasAttachments: computed(() => store.items().length > 0),
    /**
     * Whether nothing is still on its way. A refused or failed file counts as
     * settled: it will not change again, and holding Send hostage to it would leave
     * the user with no way to send at all.
     */
    isSettled: computed(() => store.items().every((item) => !isBusy(item))),
    busyCount: computed(() => store.items().filter(isBusy).length),
  })),
  withComputed(({ items }) => ({
    /**
     * What a screen reader is told about the attachments, as a whole.
     *
     * A summary rather than a per-file announcement, and deliberately free of the
     * transfer percentage: a live region that re-reads on every progress event is
     * unusable. It exists because a chip appearing is otherwise silent — the chip's
     * own `role="status"` is inserted *together with* its content, which is not
     * reliably announced.
     */
    attachmentSummary: computed(() => {
      const rows = items();
      if (rows.length === 0) {
        return '';
      }

      const counts = {
        ready: rows.filter((item) => item.attachment.state.kind === 'ready').length,
        failed: rows.filter(
          (item) =>
            item.attachment.state.kind === 'failed' || item.attachment.state.kind === 'unsupported',
        ).length,
      };

      const parts = [`${rows.length} file${rows.length === 1 ? '' : 's'} attached`];
      if (counts.ready > 0) {
        parts.push(`${counts.ready} ready`);
      }
      if (counts.failed > 0) {
        parts.push(`${counts.failed} could not be used`);
      }

      return parts.join(', ');
    }),
  })),
  withComputed(({ isSettled, target }) => ({
    /**
     * Whether Send must wait.
     *
     * Only once a target exists. Before that the files are deferred *because* no
     * conversation exists yet, and Send is the thing that creates one — blocking it
     * would deadlock the first turn of every new conversation against its own
     * attachments.
     */
    blocksSend: computed(() => target() !== null && !isSettled()),
  })),
  withMethods((store) => {
    /** Announces that nothing is outstanding, so a waiting `settled$()` is released. */
    function announceIfSettled(): void {
      if (store.isSettled()) {
        store._settled$.next();
      }
    }

    /** Replaces one item's state, then reports if that was the last one outstanding. */
    function setState(id: string, state: AttachmentState): void {
      patchState(store, replaceState(id, state));
      announceIfSettled();
      pump();
    }

    /** Drops whatever request this file has open, keeping its slot in the budget. */
    function unsubscribe(id: string): void {
      store._subscriptions.get(id)?.unsubscribe();
      store._subscriptions.delete(id);
    }

    function cancel(id: string): void {
      unsubscribe(id);
      store._active.delete(id);
    }

    function forget(id: string): void {
      cancel(id);
      dismissToastFor(id);
      store._files.delete(id);
      store._waiting.delete(id);
    }

    /**
     * Calls off the job behind a cancelled file, when there is one to call off.
     *
     * Must run before the item is dropped: `items` is the only record of the job id, and
     * the chip is what carries it.
     */
    function cancelJob(id: string): void {
      const item = store.items().find((candidate) => candidate.attachment.id === id);
      if (item === undefined || item.jobId === null) {
        return;
      }

      // An allow-list, because the endpoint deletes the document of a job that already
      // finished: only a file still on its way has anything to call off. `ready` is the
      // case that makes a deny-list unsafe — its `documentId` can be null on the wire, and
      // the chat screen retires ready chips on its own, so a miss here would delete an
      // ingested document with no user action at all. `unknown` is excluded for the same
      // reason: a failed status read may be hiding a success.
      const { kind } = item.attachment.state;
      if (item.documentId !== null || (kind !== 'uploading' && kind !== 'processing')) {
        return;
      }

      store._cancels.cancel(item.jobId);
    }

    /**
     * Drops every chip and the work behind it.
     *
     * Deliberately does **not** cancel. It is reached from a target change, from sign-out
     * and from teardown — and none of those is a retraction: the files were attached to
     * that parent on purpose, and leaving one conversation must not stop the ingestion
     * the user started there.
     */
    function clearAll(): void {
      for (const subscription of store._subscriptions.values()) {
        subscription.unsubscribe();
      }
      for (const id of [...store._fileToasts.keys()]) {
        dismissToastFor(id);
      }
      store._subscriptions.clear();
      store._active.clear();
      store._waiting.clear();
      store._files.clear();
      patchState(store, { items: [] });
      // An emptied list is a settled one. Without this a `TurnStore` gate that was
      // already waiting when the user moved to another conversation would never be
      // released, and `exhaustMap` would silently drop every later send.
      store._settled$.next();
    }

    /**
     * Starts as many queued uploads as the concurrency budget allows.
     *
     * A cap rather than "start them all", because the picker takes `multiple` and a
     * drop can carry a folder's worth: fifty concurrent XHRs plus fifty poll loops
     * would saturate the origin's connection budget, queue the status reads behind
     * the transfers, and make every chip look frozen — while competing with the SSE
     * turn on the same origin.
     */
    function pump(): void {
      for (const id of store._waiting) {
        if (store._active.size >= MAX_CONCURRENT_UPLOADS) {
          return;
        }

        store._waiting.delete(id);
        begin(id);
      }
    }

    /** Queues one file, or starts it immediately if there is room. */
    function start(id: string): void {
      if (!store._files.has(id) || store.target() === null) {
        return;
      }

      patchState(store, markStarted(id));
      setState(id, { kind: 'uploading', percent: 0 });
      store._waiting.add(id);
      pump();
    }

    function begin(id: string): void {
      const file = store._files.get(id);
      const target = store.target();
      if (file === undefined || target === null) {
        return;
      }

      cancel(id);
      store._active.add(id);

      const subscription = store._client
        .upload(target, file)
        .pipe(takeUntil(store._signedOut$))
        .subscribe({
          next: (event) => {
            if (event.kind === 'progress') {
              // Clamped upward: the server's own progress is monotonic and the
              // chip's must be too, so a re-reported `loaded` can never make the
              // bar retreat and read as the upload restarting.
              const previous = percentOf(store.items(), id);
              setState(id, { kind: 'uploading', percent: Math.max(previous, event.percent) });

              return;
            }

            patchState(store, setJobId(id, event.jobId));
            // The server seeds every job with this message, so it is what a first
            // poll would have shown anyway — and the chip is never blank between
            // the 202 and the first status read.
            setState(id, { kind: 'processing', subStatus: 'Waiting to start' });
            follow(id, event.jobId);
          },
          error: (error: AppError) => {
            release(id);

            // Cancelling is the user's own doing — `remove` has already taken the
            // chip off screen, and announcing it would report their action as a
            // failure.
            if (error.kind === 'aborted') {
              return;
            }

            reject(id, file, error);
          },
          complete: () => release(id),
        });

      // Assigned after `subscribe` returns, so a transport that ever settled
      // synchronously would have run `release` before this line and left a closed
      // subscription in the map. Guarded by only storing a live one.
      // Guarded on both counts: a transport that settled synchronously would have
      // run `release` already, and one that delivered `accepted` synchronously would
      // have let `follow` install the poll subscription under this id — which this
      // line would then orphan.
      if (!subscription.closed && !store._subscriptions.has(id)) {
        store._subscriptions.set(id, subscription);
      }
    }

    /** Frees this file's slot in the concurrency budget and starts the next. */
    function release(id: string): void {
      store._subscriptions.delete(id);
      store._active.delete(id);
      pump();
    }

    /** Renders one refused upload on its chip and in a toast (US-805). */
    function reject(id: string, file: File, error: AppError): void {
      const rejection = describeUploadRejection(error, file);

      setState(id, { kind: 'failed', reason: rejection.detail });
      const toastId = store._toasts.show({
        tone: 'error',
        title: rejection.title,
        detail: rejection.detail,
        traceLine: rejection.traceLine,
        // Persists until dismissed, because `AUTO_DISMISS_MS.error` is zero: an error
        // that disappears before it is read is a defect (frame `4l`).
        onRetry: rejection.retryable ? () => retryUpload(id) : undefined,
      });

      // Tracked so the toast can be withdrawn with its chip. `ToastStore` is
      // root-scoped and its errors never self-dismiss, so a toast whose file has been
      // removed — or whose whole screen has gone — would outlive the bytes its Retry
      // needs, and pressing it would silently do nothing.
      dismissToastFor(id);
      store._fileToasts.set(id, toastId);
    }

    /** Withdraws the failure toast belonging to one file, if it still has one. */
    function dismissToastFor(id: string): void {
      const toastId = store._fileToasts.get(id);
      if (toastId !== undefined) {
        store._toasts.dismiss(toastId);
        store._fileToasts.delete(id);
      }
    }

    function retryUpload(id: string): void {
      patchState(store, setJobId(id, null), setDocumentId(id, null));
      start(id);
    }

    /** Follows one job to its conclusion, rendering every step (US-802, US-803). */
    function follow(id: string, jobId: string): void {
      // Unsubscribes the upload before its own `complete` can fire, which would
      // otherwise delete the poll subscription installed a line below. The slot is
      // deliberately **kept**: ingest counts against the same budget as the transfer.
      unsubscribe(id);

      const subscription = store._status
        .follow(jobId)
        .pipe(takeUntil(store._signedOut$))
        .subscribe({
          next: (poll) => {
            if (poll.kind === 'expired') {
              // Not a failure, and never rendered as one: nothing went wrong, the
              // status simply is no longer kept — and the same 404 covers another
              // user's job, which the client must not tell apart either (US-803).
              release(id);
              setState(id, { kind: 'unknown' });

              return;
            }

            const { state, documentId, errorMessage } = poll.status;

            if (state === JOB_STATE.succeeded) {
              release(id);
              // Retained so US-804 can fetch the file back with no further lookup.
              patchState(store, setDocumentId(id, documentId));
              setState(id, { kind: 'ready' });

              return;
            }

            if (state === JOB_STATE.failed) {
              release(id);
              setState(id, {
                kind: 'failed',
                // Already sanitized server-side: FluentValidation messages pass
                // through, every other exception message is replaced, because
                // Azure SDK text routinely embeds credential-bearing URLs.
                reason: errorMessage ?? 'Processing failed. Try uploading the file again.',
              });

              return;
            }

            setState(id, { kind: 'processing', subStatus: subStatusOf(poll.status) });
          },
          error: (error: AppError) => {
            release(id);
            // The file may well have been ingested — only the *status read* failed —
            // so this says the state is unknown rather than claiming a failure.
            setState(id, { kind: 'unknown' });
            store._toasts.fromError(error);
          },
          complete: () => release(id),
        });

      // Guarded on both counts: a transport that settled synchronously would have
      // run `release` already, and one that delivered `accepted` synchronously would
      // have let `follow` install the poll subscription under this id — which this
      // line would then orphan.
      if (!subscription.closed && !store._subscriptions.has(id)) {
        store._subscriptions.set(id, subscription);
      }
    }

    /**
     * Points the store at the parent its files hang off, draining anything attached
     * before one existed.
     *
     * Idempotent for the same target, because both the create path and the route call
     * it. A **different** target is a different draft, so its chips go: they describe
     * files that belong to whatever the user just left. Comparing `kind` as well as
     * `id` is not pedantry — a conversation and a project are separate id spaces, so
     * only the pair identifies a parent.
     */
    function bindTarget(target: UploadTarget | null): void {
      const current = store.target();
      if (current?.kind === target?.kind && current?.id === target?.id) {
        return;
      }

      if (current !== null || target === null) {
        clearAll();
      }

      patchState(store, { target });

      if (target === null) {
        return;
      }

      if (store._startsOnAttach.value) {
        drainPending();
      }
    }

    /** Hands every file that has not been posted yet to the transport. */
    function drainPending(): void {
      for (const item of store.items()) {
        if (!item.started && item.jobId === null && item.attachment.state.kind === 'uploading') {
          start(item.attachment.id);
        }
      }
    }

    return {
      /**
       * Points the store at a conversation (US-801).
       *
       * A `signalMethod` because the chat route binds it to the route parameter, which
       * is `undefined` on `/chat` — an unsaved conversation whose uploads defer until
       * US-401's create hands over an id.
       *
       * @param conversationId The open conversation, or `undefined` for an unsaved one.
       */
      bindConversation: signalMethod<string | undefined>((conversationId) => {
        bindTarget(
          conversationId === undefined ? null : { kind: 'conversation', id: conversationId },
        );
      }),

      /**
       * Points the store at a project (US-905).
       *
       * A plain method rather than a `signalMethod`: the files panel only renders under
       * an already-resolved `:id`, so there is no absent state to model and no signal
       * to track.
       *
       * @param projectId The project whose files this panel manages.
       */
      bindProject(projectId: string): void {
        bindTarget({ kind: 'project', id: projectId });
      },

      /**
       * Takes files from the picker or a drop, refusing what this deployment cannot
       * read before anything is sent (US-805).
       *
       * @param files The files the user chose.
       */
      attach(files: readonly File[]): void {
        const supportedLabel = store._extensions.supportedLabel();

        for (const file of files) {
          const id = `attachment-${store._sequence.value++}`;
          const supported = store._extensions.isSupported(file.name);

          if (supported) {
            store._files.set(id, file);
          }

          patchState(
            store,
            append({
              attachment: {
                id,
                fileName: file.name,
                sizeLabel: formatBytes(file.size),
                state: supported
                  ? { kind: 'uploading', percent: 0 }
                  : { kind: 'unsupported', reason: unsupportedReason(file.name, supportedLabel) },
              },
              jobId: null,
              documentId: null,
              started: false,
            }),
          );

          if (supported && store._startsOnAttach.value && store.target() !== null) {
            start(id);
          }
        }
      },

      /**
       * Takes one chip off screen, cancelling its transfer if there is still one to
       * cancel.
       *
       * @param id The attachment id.
       */
      remove(id: string): void {
        cancelJob(id);
        forget(id);
        patchState(store, drop(id));
        announceIfSettled();
        pump();
      },

      /**
       * Cancels every file genuinely on its way, for the composer's Stop control.
       *
       * Two kinds of chip are deliberately left alone. A terminal one is not work in
       * flight, and dropping it would take away a failure nobody has read yet; a
       * *deferred* one — attached before its parent exists — has no request behind it
       * either, and Stop was never offered for it, so discarding it would lose the
       * user's file to a press aimed at something else.
       */
      cancelAll(): void {
        for (const item of [...store.items()]) {
          if (isBusy(item)) {
            cancelJob(item.attachment.id);
            forget(item.attachment.id);
            patchState(store, drop(item.attachment.id));
          }
        }

        announceIfSettled();
        pump();
      },

      /**
       * The bytes behind chips no server has taken yet, in the order they were attached.
       *
       * For the handoff out of a screen that is about to be destroyed. Mapped over
       * `items` rather than read off `_files`, so the order is the one on screen and a
       * chip already removed cannot travel.
       */
      pendingFiles(): readonly File[] {
        // Only files nothing has been posted for. A started one may have a transfer in
        // flight whose `jobId` has not landed yet, and handing that on would ingest it
        // twice.
        return store
          .items()
          .filter((item) => !item.started && item.documentId === null)
          .map((item) => store._files.get(item.attachment.id))
          .filter((file): file is File => file !== undefined);
      },

      /**
       * Re-posts a file whose upload or ingestion failed, under a new job.
       *
       * @param id The attachment id.
       */
      retry(id: string): void {
        retryUpload(id);
      },

      /**
       * Holds every attached file back until {@link startPending} asks for it.
       *
       * What a composer wants: attaching states an intention, and Send is the instruction.
       * Without it a conversation that already exists uploads on the drop, so the first
       * turn of a conversation would wait for Send and every later one would not.
       */
      deferUploadsUntilSend(): void {
        store._startsOnAttach.value = false;
      },

      /**
       * Starts whatever is still waiting, once its parent exists.
       *
       * Called by the send path rather than by the composer, so a turn cannot open its
       * stream before the files it is about have been posted — {@link settled$} then
       * holds it until they finish.
       */
      startPending(): void {
        drainPending();
      },

      /**
       * The persisted document behind a chip, once there is one.
       *
       * @param id The attachment id.
       */
      documentIdOf(id: string): string | null {
        return (
          store.items().find((candidate) => candidate.attachment.id === id)?.documentId ?? null
        );
      },

      /**
       * Fetches this chip's file back (US-804).
       *
       * The signed link is requested here, at the click, and never on render — see
       * {@link DocumentDownloadStore}.
       *
       * @param id The attachment id.
       */
      download(id: string): void {
        const item = store.items().find((candidate) => candidate.attachment.id === id);
        const target = store.target();
        if (item?.documentId == null || target === null) {
          return;
        }

        store._downloads.download(target, item.documentId, item.attachment.fileName);
      },

      /**
       * Emits once, when nothing is outstanding.
       *
       * `TurnStore` waits on this before opening the stream, so the answer is grounded
       * in the files the prompt is about. Already-settled is the common case and
       * completes synchronously, which keeps a turn with no attachments exactly as
       * fast as it was before EP-8.
       */
      settled$(): Observable<void> {
        return store.isSettled() ? of(undefined) : store._settled$.pipe(take(1));
      },
    };
  }),
  withHooks({
    onInit(store) {
      // `withResetOnSignOut` clears state and nothing else, so the handles it cannot
      // see are released here — otherwise the previous user's files stay in memory and
      // their in-flight uploads keep going.
      store._signedOut$.pipe(takeUntilDestroyed()).subscribe(() => {
        for (const subscription of store._subscriptions.values()) {
          subscription.unsubscribe();
        }
        store._subscriptions.clear();
        store._active.clear();
        store._waiting.clear();
        store._files.clear();
        // `ToastStore` clears its own on sign-out; only the mapping is ours to drop.
        store._fileToasts.clear();
        // Deliberately no `_settled$.next()` here. Releasing the gate would drive
        // `TurnStore`'s pipeline *forward* into a stream request for a session that
        // has just ended; every waiter already cancels on `signedOut$` itself, which
        // is the correct teardown and does not depend on which store's handler the
        // event dispatcher happens to reach first.
      });
    },
    onDestroy(store) {
      for (const subscription of store._subscriptions.values()) {
        subscription.unsubscribe();
      }
      // The toasts outlive this screen — `ToastStore` is root-scoped — but their
      // Retry cannot: the bytes it would re-post are about to be dropped.
      for (const toastId of store._fileToasts.values()) {
        store._toasts.dismiss(toastId);
      }
      store._fileToasts.clear();
      store._subscriptions.clear();
      store._active.clear();
      store._waiting.clear();
      store._files.clear();
      store._settled$.complete();
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);

function append(item: UploadItem): PartialStateUpdater<UploadState> {
  return ({ items }) => ({ items: [...items, item] });
}

function drop(id: string): PartialStateUpdater<UploadState> {
  return ({ items }) => ({ items: items.filter((item) => item.attachment.id !== id) });
}

function replaceState(id: string, state: AttachmentState): PartialStateUpdater<UploadState> {
  return ({ items }) => ({
    items: items.map((item) =>
      item.attachment.id === id ? { ...item, attachment: { ...item.attachment, state } } : item,
    ),
  });
}

function setJobId(id: string, jobId: string | null): PartialStateUpdater<UploadState> {
  return ({ items }) => ({
    items: items.map((item) => (item.attachment.id === id ? { ...item, jobId } : item)),
  });
}

function markStarted(id: string): PartialStateUpdater<UploadState> {
  return ({ items }) => ({
    items: items.map((item) => (item.attachment.id === id ? { ...item, started: true } : item)),
  });
}

function setDocumentId(id: string, documentId: string | null): PartialStateUpdater<UploadState> {
  return ({ items }) => ({
    items: items.map((item) => (item.attachment.id === id ? { ...item, documentId } : item)),
  });
}

/** The percentage already on screen, so a re-report cannot make the bar retreat. */
function percentOf(items: readonly UploadItem[], id: string): number {
  const state = items.find((item) => item.attachment.id === id)?.attachment.state;

  return state?.kind === 'uploading' ? state.percent : 0;
}

function isTerminal(state: AttachmentState): boolean {
  return state.kind !== 'uploading' && state.kind !== 'processing';
}

/** Whether a request is actually on its way for this file. */
function isBusy(item: UploadItem): boolean {
  return item.started && !isTerminal(item.attachment.state);
}

/**
 * Why a file was refused, naming both the extension and what would have worked.
 *
 * The supported set is built from `GET api/documents/file-extensions` rather than
 * copied from frame `4l`, whose example string names two formats the API does not
 * accept and omits one it does.
 */
function unsupportedReason(fileName: string, supportedLabel: string): string {
  const extension = extensionOf(fileName);
  const refused =
    extension === '' ? 'Files need a recognised extension' : `${extension} isn’t supported`;

  return supportedLabel === '' ? `${refused}.` : `${refused} — ${supportedLabel}`;
}
