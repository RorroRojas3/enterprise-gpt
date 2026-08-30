import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { DOCUMENT, inject } from '@angular/core';
import { patchState, signalStore, withMethods, withProps, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { exhaustMap, takeUntil } from 'rxjs';
import { EXPORT_FORMAT_LABELS, ExportFormat } from '@domain/api/conversation-export';
import { saveWithAnchor } from '@core/downloads/anchor-download';
import { toAppErrorFromBlobError } from '@core/errors/to-app-error-from-blob';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { skipRetry } from '@core/http/interceptors/retry.interceptor';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

/** Which conversation and format an export request is for. */
export interface ExportRequest {
  readonly conversationId: string;
  readonly format: ExportFormat;
}

/**
 * How a request settled, replaced rather than consumed.
 *
 * The menu closes on a success and stays open on a failure, which it cannot decide from
 * `pending` going null alone. A one-shot the reader nulls
 * back would not do either: the composer and the conversation header both mount a menu at
 * desktop widths, and whichever effect ran first would swallow the outcome for the other.
 * So this is a replaced record with a sequence number, and each menu tracks the last it
 * acted on.
 */
export interface ExportOutcome extends ExportRequest {
  readonly ok: boolean;
  /** Increments per settle, so two mounted menus each react exactly once. */
  readonly seq: number;
}

interface ExportState {
  /**
   * The export in flight, or null.
   *
   * One at a time, because the menu dims its other items while one is preparing — there is
   * no surface for a second concurrent export to render into.
   */
  readonly pending: ExportRequest | null;
  readonly outcome: ExportOutcome | null;
}

const initialState: ExportState = { pending: null, outcome: null };

/**
 * Downloads a conversation as a file.
 *
 * Shaped after `DocumentDownloadStore`, which is this repository's reference for "fetch on
 * click, never on render, and let go of the artefact immediately" — but the transport is
 * the opposite way round and that drives every difference. A document download is a signed
 * URL the browser follows; an export is **bytes this API returns**, because rendering to
 * blob storage would make every export, markdown included, depend on storage and leave
 * rendered transcripts behind for something to clean up.
 *
 * Three consequences:
 *
 * - **`responseType: 'blob'`**, so the response never goes through JSON parsing. That is
 *   also why the request opts out of `retryInterceptor`: an error body arrives as a `Blob`
 *   the interceptor cannot classify, so an application-typed 503 would be replayed three
 *   times as though it were a gateway hiccup.
 * - **The failure body is read back by hand**, through `toAppErrorFromBlobError`. Without
 *   it every application problem type on this request degrades to the plain `http` arm,
 *   which loses exactly the distinction the typed 503 exists to draw.
 * - **The object URL is revoked**, and the blob is never written to state. A transcript is
 *   the most sensitive thing this app holds, and a URL left alive keeps the whole document
 *   in memory for the life of the page.
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

    /**
     * Hands the rendered bytes to the browser.
     *
     * The name comes from `Content-Disposition` when the header survives — the API's CORS
     * policy exposes it — and from the conversation's own id otherwise, which is ugly but
     * saves a file rather than saving nothing.
     */
    function save(response: HttpResponse<Blob>, request: ExportRequest): void {
      const blob = response.body;
      if (blob === null) {
        throw new Error('The export response carried no body.');
      }

      const url = URL.createObjectURL(blob);

      try {
        saveWithAnchor(store._document, url, fileNameOf(response, request));
      } finally {
        // Never synchronously: the click is dispatched synchronously, but the browser
        // reads the URL afterwards and revoking first cancels the download it was just
        // handed. Not on the next task either — some engines have not begun reading by
        // then, which produces a download that silently does nothing and reproduces on
        // nobody's machine. The blob is referenced by nothing else, so waiting costs only
        // the delay in releasing it.
        setTimeout(() => URL.revokeObjectURL(url), REVOKE_DELAY_MS);
      }
    }

    const _download = rxMethod<ExportRequest>(
      // exhaustMap, not mergeMap: a second press while one export is out is a double-click,
      // not a second file — and unlike a document list there is one conversation here, so
      // the second press is for the same document the first is already rendering.
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
                // Awaited nowhere: the toast is raised when the body has been read, and
                // nothing downstream waits on it. `fromError` is what carries the traceId.
                void toAppErrorFromBlobError(cause, url).then((error) =>
                  store._toasts.fromError(error),
                );
              },
            }),
          );
      }),
    );

    return {
      /**
       * Renders and downloads a conversation. Call from a click, never from a render.
       *
       * @param conversationId The conversation to export.
       * @param format The format the reader chose.
       */
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

/**
 * Reads the download name the server signed, falling back to one built here.
 *
 * Prefers RFC 5987's `filename*` over the plain `filename`, because a conversation named
 * in any script travels only in the encoded form.
 */
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
