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
import { JOB_STATE, UploadTarget } from '@core/api/document.model';
import { DocumentDownloadStore } from '@core/documents/document-download-store';
import { AppError } from '@core/errors/app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';
import {
  Attachment,
  AttachmentRemoveAction,
  AttachmentState,
} from '@shared/files/attachment.model';
import { formatBytes } from '@shared/formatting/bytes';
import { DocumentUploadClient } from './document-upload-client';
import { SupportedExtensionsStore, extensionOf } from './supported-extensions-store';
import { UploadCancelClient } from './upload-cancel-client';
import { describeUploadRejection } from './upload-rejection';
import { subStatusOf } from './upload-schedule';
import { UploadStatusClient } from './upload-status-client';

/**
 * `attachment` is what the chip renders; the rest is plumbing. Replaced on every transition,
 * never mutated, or the chip silently stops updating.
 */
export interface UploadItem {
  readonly attachment: Attachment;
  /** Present once the server has queued the job; the key the status route polls. */
  readonly jobId: string | null;
  /** Present once the job has succeeded; downloads use it without a lookup. */
  readonly documentId: string | null;
  /**
   * Whether the file has been handed to the transport. A deferred file sits in `uploading`
   * with nothing in flight, so state alone would hold Send against a request nobody made.
   */
  readonly started: boolean;
}

/** One chip, with every decision the template would otherwise have to ask for. */
export interface AttachmentRow extends Attachment {
  readonly removeAction: AttachmentRemoveAction;
  /**
   * A request is actually in flight. A file deferred until its parent exists is `uploading`
   * but not busy, and has nothing for Stop to call off.
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
 * Files in flight at once, transfer and ingest together: HTTP/1.1 allows six connections per
 * origin, shared with the SSE turn, and ingest is the longer phase. A cancel frees its slot
 * immediately, without waiting for the server.
 */
const MAX_CONCURRENT_UPLOADS = 3;

/**
 * Attached files and their journey to being usable. Component-scoped: chat and the project
 * files panel each run an independent instance. A composer posts its files on Send and waits
 * on {@link settled$}, since a file posted as the stream opens is invisible to that turn.
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
    /**
     * Kept for the deferred queue and Retry. In props, not state: a Map in state is either mutated
     * without a signal write or replaced, re-rendering every chip per progress event.
     */
    _files: new Map<string, File>(),
    _subscriptions: new Map<string, Subscription>(),
    /** Files holding a slot against {@link MAX_CONCURRENT_UPLOADS}. */
    _active: new Set<string>(),
    /** Files waiting for a slot. Insertion order is attach order. */
    _waiting: new Set<string>(),
    /** The failure toast each file currently owns, so it can be withdrawn with it. */
    _fileToasts: new Map<string, string>(),
    _sequence: { value: 0 },
    /** Fires whenever the last unsettled item settles. See {@link settled$}. */
    _settled$: new Subject<void>(),
    /** False for a composer, where Send rather than attaching is the instruction. */
    _startsOnAttach: { value: true },
  })),
  withComputed((store) => ({
    /**
     * Precomputed per row: a per-chip lookup bound in the template would run O(n²) on every
     * progress event.
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
    /** Failed and refused files count as settled, or Send could never be pressed again. */
    isSettled: computed(() => store.items().every((item) => !isBusy(item))),
    busyCount: computed(() => store.items().filter(isBusy).length),
  })),
  withComputed(({ items }) => ({
    /**
     * Live-region summary, since a new chip's own `role="status"` is not reliably announced.
     * No percentages: re-reading on every progress event would be unusable.
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
    /** Only with a target: before one, Send is what creates it, so blocking would deadlock. */
    blocksSend: computed(() => target() !== null && !isSettled()),
  })),
  withMethods((store) => {
    /** Announces that nothing is outstanding, so a waiting `settled$()` is released. */
    function announceIfSettled(): void {
      if (store.isSettled()) {
        store._settled$.next();
      }
    }

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

    /** Must run before the item is dropped: the item is the only record of its job id. */
    function cancelJob(id: string): void {
      const item = store.items().find((candidate) => candidate.attachment.id === id);
      if (item === undefined || item.jobId === null) {
        return;
      }

      // An allow-list, because the endpoint deletes a finished job's document: a `ready` chip
      // can carry a null `documentId`, and `unknown` may be hiding a success.
      const { kind } = item.attachment.state;
      if (item.documentId !== null || (kind !== 'uploading' && kind !== 'processing')) {
        return;
      }

      store._cancels.cancel(item.jobId);
    }

    /**
     * Drops every chip without cancelling: a target change, sign-out or teardown is not a
     * retraction, and must not stop ingestion the user started there.
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
      // An emptied list is settled; without this a waiting `TurnStore` gate never releases
      // and `exhaustMap` drops every later send.
      store._settled$.next();
    }

    /** Starts as many queued uploads as {@link MAX_CONCURRENT_UPLOADS} allows. */
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
              // Clamped upward, so a re-reported `loaded` never makes the bar retreat.
              const previous = percentOf(store.items(), id);
              setState(id, { kind: 'uploading', percent: Math.max(previous, event.percent) });

              return;
            }

            patchState(store, setJobId(id, event.jobId));
            // The server seeds every job with this message, so the chip is never blank
            // before the first status read.
            setState(id, { kind: 'processing', subStatus: 'Waiting to start' });
            follow(id, event.jobId);
          },
          error: (error: AppError) => {
            release(id);

            // A cancel is the user's own doing, and `remove` has already taken the chip away.
            if (error.kind === 'aborted') {
              return;
            }

            reject(id, file, error);
          },
          complete: () => release(id),
        });

      // A synchronous transport may already have run `release`, or delivered `accepted` and
      // let `follow` install its poll subscription under this id.
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

    /** Renders one refused upload on its chip and in a toast. */
    function reject(id: string, file: File, error: AppError): void {
      const rejection = describeUploadRejection(error, file);

      setState(id, { kind: 'failed', reason: rejection.detail });
      const toastId = store._toasts.show({
        tone: 'error',
        title: rejection.title,
        detail: rejection.detail,
        traceLine: rejection.traceLine,
        onRetry: rejection.retryable ? () => retryUpload(id) : undefined,
      });

      // Withdrawn with its chip: error toasts never self-dismiss and `ToastStore` is
      // root-scoped, so an orphaned toast's Retry would silently do nothing.
      dismissToastFor(id);
      store._fileToasts.set(id, toastId);
    }

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

    /** Follows one job to its conclusion, rendering every step. */
    function follow(id: string, jobId: string): void {
      // Drops the upload before its `complete` can delete the poll subscription installed
      // below. The slot is kept: ingest counts against the same budget as the transfer.
      unsubscribe(id);

      const subscription = store._status
        .follow(jobId)
        .pipe(takeUntil(store._signedOut$))
        .subscribe({
          next: (poll) => {
            if (poll.kind === 'expired') {
              // Not a failure: the status is no longer kept, and the same 404 covers
              // another user's job, which the client must not tell apart.
              release(id);
              setState(id, { kind: 'unknown' });

              return;
            }

            const { state, documentId, errorMessage } = poll.status;

            if (state === JOB_STATE.succeeded) {
              release(id);
              patchState(store, setDocumentId(id, documentId));
              setState(id, { kind: 'ready' });

              return;
            }

            if (state === JOB_STATE.failed) {
              release(id);
              setState(id, {
                kind: 'failed',
                // Sanitized server-side, since Azure SDK text can embed credential-bearing
                // URLs; only FluentValidation messages pass through.
                reason: errorMessage ?? 'Processing failed. Try uploading the file again.',
              });

              return;
            }

            setState(id, { kind: 'processing', subStatus: subStatusOf(poll.status) });
          },
          error: (error: AppError) => {
            release(id);
            // Only the status read failed; the file may well have been ingested.
            setState(id, { kind: 'unknown' });
            store._toasts.fromError(error);
          },
          complete: () => release(id),
        });

      // A poll that settles synchronously has already run `release`.
      if (!subscription.closed && !store._subscriptions.has(id)) {
        store._subscriptions.set(id, subscription);
      }
    }

    /**
     * Idempotent for the same target, since both the create path and the route call it. A
     * different target is a different draft, so its chips go. Conversation and project ids
     * are separate spaces, hence comparing `kind` too.
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

    function drainPending(): void {
      for (const item of store.items()) {
        if (!item.started && item.jobId === null && item.attachment.state.kind === 'uploading') {
          start(item.attachment.id);
        }
      }
    }

    return {
      /**
       * A `signalMethod` bound to the route parameter, which is `undefined` on `/chat`; uploads
       * defer until the create hands over an id.
       */
      bindConversation: signalMethod<string | undefined>((conversationId) => {
        bindTarget(
          conversationId === undefined ? null : { kind: 'conversation', id: conversationId },
        );
      }),

      /** A plain method: the files panel only renders under a resolved `:id`. */
      bindProject(projectId: string): void {
        bindTarget({ kind: 'project', id: projectId });
      },

      /** Refuses what this deployment cannot read before anything is sent. */
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

      /** Takes one chip off screen, cancelling its job if it is still on its way. */
      remove(id: string): void {
        cancelJob(id);
        forget(id);
        patchState(store, drop(id));
        announceIfSettled();
        pump();
      },

      /**
       * Cancels every busy file, for the composer's Stop. Terminal chips keep their unread
       * failure and deferred ones have no request behind them, so both are left alone.
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
       * Unposted files in attach order, for handing off from a screen about to be destroyed.
       * Mapped over `items`, so the order is the on-screen one and a removed chip cannot travel.
       */
      pendingFiles(): readonly File[] {
        // A started file's `jobId` may not have landed yet; handing it on would ingest it twice.
        return store
          .items()
          .filter((item) => !item.started && item.documentId === null)
          .map((item) => store._files.get(item.attachment.id))
          .filter((file): file is File => file !== undefined);
      },

      /** Re-posts a file whose upload or ingestion failed, under a new job. */
      retry(id: string): void {
        retryUpload(id);
      },

      /**
       * Holds files until {@link startPending}, so a composer uploads on Send whether or not
       * its conversation exists yet.
       */
      deferUploadsUntilSend(): void {
        store._startsOnAttach.value = false;
      },

      /** Called by the send path, so a turn cannot stream before its files are posted. */
      startPending(): void {
        drainPending();
      },

      documentIdOf(id: string): string | null {
        return (
          store.items().find((candidate) => candidate.attachment.id === id)?.documentId ?? null
        );
      },

      /** Requests the signed link at the click, never on render. */
      download(id: string): void {
        const item = store.items().find((candidate) => candidate.attachment.id === id);
        const target = store.target();
        if (item?.documentId == null || target === null) {
          return;
        }

        store._downloads.download(target, item.documentId, item.attachment.fileName);
      },

      /**
       * Emits once nothing is outstanding; `TurnStore` waits on it before streaming. Completes
       * synchronously when already settled, so a turn without files pays nothing.
       */
      settled$(): Observable<void> {
        return store.isSettled() ? of(undefined) : store._settled$.pipe(take(1));
      },
    };
  }),
  withHooks({
    onInit(store) {
      // `withResetOnSignOut` clears state only; without this the previous user's files stay
      // in memory and their uploads keep going.
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
        // No `_settled$.next()`: it would drive `TurnStore` into a stream request for an
        // ended session. Every waiter already cancels on `signedOut$`.
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

function isBusy(item: UploadItem): boolean {
  return item.started && !isTerminal(item.attachment.state);
}

/** Names the refused extension and the supported set, which comes from the API. */
function unsupportedReason(fileName: string, supportedLabel: string): string {
  const extension = extensionOf(fileName);
  const refused =
    extension === '' ? 'Files need a recognised extension' : `${extension} isn’t supported`;

  return supportedLabel === '' ? `${refused}.` : `${refused} — ${supportedLabel}`;
}
