import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { DOCUMENT, inject } from '@angular/core';
import { tapResponse } from '@ngrx/operators';
import { patchState, signalStore, withMethods, withProps, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { exhaustMap, takeUntil } from 'rxjs';
import { EXPORT_FORMAT_LABELS, ExportFormat } from '@shared/models/api/conversation-export.model';
import { saveWithAnchor } from '@shared/utils/downloads/anchor-download';
import { toAppErrorFromBlobError } from '@core/errors/to-app-error-from-blob';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url-service';
import { skipRetry } from '@core/http/interceptors/retry.interceptor';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

export interface ExportRequest {
  readonly conversationId: string;
  readonly format: ExportFormat;
}

/**
 * Replaced, not consumed: the composer and header can both mount a menu, and a one-shot outcome
 * would be swallowed by whichever effect ran first.
 */
export interface ExportOutcome extends ExportRequest {
  readonly ok: boolean;
  /** Increments per settle, so two mounted menus each react exactly once. */
  readonly seq: number;
}

interface ExportState {
  /** One at a time: the menu dims its other items, so a second export has nowhere to render. */
  readonly pending: ExportRequest | null;
  readonly outcome: ExportOutcome | null;
}

const initialState: ExportState = { pending: null, outcome: null };

/**
 * Exports are bytes from the API, not a signed URL. Retry is skipped because the interceptor
 * cannot classify a Blob error body, which `toAppErrorFromBlobError` reads back instead. The
 * object URL is revoked and the blob never stored: a transcript is the most sensitive data here.
 */
export const ConversationExportStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _toasts: inject(ToastStore),
    _document: inject(DOCUMENT),
    _signedOut$: injectSignedOut(),
    _sequence: { value: 0 },
  })),
  withMethods((store) => {
    function settle(request: ExportRequest, ok: boolean): void {
      patchState(store, {
        pending: null,
        outcome: { ...request, ok, seq: ++store._sequence.value },
      });
    }

    /** Names the file from `Content-Disposition` (CORS-exposed), else the conversation id. */
    function save(response: HttpResponse<Blob>, request: ExportRequest): void {
      const blob = response.body;
      if (blob === null) {
        throw new Error('The export response carried no body.');
      }

      const url = URL.createObjectURL(blob);

      try {
        saveWithAnchor(store._document, url, fileNameOf(response, request));
      } finally {
        // Delayed: revoking synchronously cancels the download, and some engines have not
        // started reading even by the next task.
        setTimeout(() => URL.revokeObjectURL(url), REVOKE_DELAY_MS);
      }
    }

    const _download = rxMethod<ExportRequest>(
      // exhaustMap: a second press while an export is out is a double-click, not a second file.
      exhaustMap((request) => {
        patchState(store, { pending: request });

        const url = store._api.build(
          `conversations/${ApiUrl.segment(request.conversationId)}/export`,
        );

        return store._http
          .get(url, {
            params: new HttpParams().set('format', request.format),
            responseType: 'blob',
            observe: 'response',
            context: skipRetry(),
          })
          .pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (response) => {
                try {
                  save(response, request);
                  settle(request, true);
                } catch {
                  store._toasts.show({
                    tone: 'error',
                    title: `${label(request.format)} download failed`,
                    detail: 'The file could not be handed to your browser.',
                    traceLine: null,
                  });
                  settle(request, false);
                }
              },
              error: (cause: unknown) => {
                settle(request, false);
                // Not awaited: nothing downstream waits on the body read that raises the toast.
                void toAppErrorFromBlobError(cause, url).then((error) =>
                  store._toasts.fromError(error),
                );
              },
            }),
          );
      }),
    );

    return {
      /** Call from a click, never from a render. */
      download(conversationId: string, format: ExportFormat): void {
        if (store.pending() !== null) {
          return;
        }

        _download({ conversationId, format });
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);

/** How long the browser is given to start reading the object URL before it is released. */
const REVOKE_DELAY_MS = 10_000;

function label(format: ExportFormat): string {
  return EXPORT_FORMAT_LABELS[format] ?? format.toUpperCase();
}

/** Prefers RFC 5987's `filename*`: a name in any script travels only in the encoded form. */
function fileNameOf(response: HttpResponse<Blob>, request: ExportRequest): string {
  const disposition = response.headers.get('Content-Disposition') ?? '';

  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  if (encoded !== undefined) {
    try {
      return decodeURIComponent(encoded);
    } catch {
      // A malformed header is not worth losing the download over.
    }
  }

  const plain = /filename="?([^";]+)"?/i.exec(disposition)?.[1];

  return plain ?? `conversation-${request.conversationId}.${request.format}`;
}
