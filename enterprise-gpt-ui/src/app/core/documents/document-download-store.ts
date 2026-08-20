import { HttpClient } from '@angular/common/http';
import { DOCUMENT, inject } from '@angular/core';
import { patchState, signalStore, withMethods, withProps } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { mergeMap, takeUntil } from 'rxjs';
import { DocumentDownloadDto, UploadTarget, documentTargetPath } from '@domain/api/document';
import { saveWithAnchor } from '@core/downloads/anchor-download';
import { AppError } from '@core/errors/app-error';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { addPendingId, removePendingId, withPendingIds } from '@core/state/with-pending-ids';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

/**
 * Fetches the original file behind a document, on click and never before (US-804).
 *
 * The response's `downloadUrl` is a **bearer credential** with roughly a five-minute
 * life: anyone holding it reads the file with no sign-in. That is what shapes this
 * store more than anything else —
 *
 * - **Nothing is prefetched.** A list of forty documents issues zero requests until one
 *   is clicked, so forty live credentials are never in flight for a user who wanted
 *   one file.
 * - **The URL never lands anywhere.** It is not written to state, `localStorage`,
 *   `sessionStorage`, a router URL, or a log; it exists inside {@link save} and is gone
 *   when that function returns. Holding it in store state would also survive into the
 *   next render for no purpose.
 * - **The link is not fetched, only followed.** It carries its own SAS credential and
 *   must not receive the app's `Authorization` header, so it deliberately does not go
 *   through `HttpClient`. The route already signs `Content-Disposition`, so the browser
 *   saves the right file name whether or not the `download` attribute survives the
 *   cross-origin hop.
 *
 * `withPendingIds` keyed by document id is what lets one row spin without disabling the
 * other thirty-nine.
 */
export const DocumentDownloadStore = signalStore(
  { providedIn: 'root' },
  withPendingIds(),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _toasts: inject(ToastStore),
    _document: inject(DOCUMENT),
    _signedOut$: injectSignedOut(),
  })),
  withMethods((store) => {
    /**
     * Hands the file to the browser and lets go of the credential immediately.
     *
     * The handoff itself is `saveWithAnchor`, shared with the conversation export
     * (US-1502); the scheme check below stays here, because it is about a URL the
     * server chose and a `blob:` URL the client made needs nothing of the kind.
     */
    function save(download: DocumentDownloadDto): boolean {
      // A direct `href` assignment bypasses Angular's `DomSanitizer` entirely, so a
      // `javascript:` value would execute on click. The value is server-issued and
      // authenticated, which makes this defence in depth rather than a live hole —
      // but it is the same standard the markdown pipeline already holds itself to,
      // and the check costs one parse.
      let parsed: URL;
      try {
        parsed = new URL(download.downloadUrl, store._document.baseURI);
      } catch {
        return false;
      }

      if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') {
        return false;
      }

      saveWithAnchor(store._document, parsed.href, download.fileName);

      return true;
    }

    function explain(error: AppError, fileName: string): void {
      switch (error.kind) {
        case 'resource-not-found':
          // Never "deleted": a 404 also covers a document that belongs to someone
          // else, and asserting deletion would leak which of the two it was.
          store._toasts.show({
            tone: 'error',
            title: `${fileName} isn’t available`,
            detail: 'This document can no longer be retrieved.',
            traceLine: null,
          });

          return;

        case 'storage-not-configured':
          // A deployment fact, not a failure of this file — and not retryable, which
          // is why `retryInterceptor` leaves an app-typed 503 alone too.
          store._toasts.show({
            tone: 'error',
            title: 'Downloads aren’t available in this deployment',
            detail: 'Document storage is not configured. Ask an administrator.',
            traceLine: null,
          });

          return;

        default:
          store._toasts.fromError(error);
      }
    }

    const _download = rxMethod<{ target: UploadTarget; documentId: string; fileName: string }>(
      // mergeMap, not switchMap: two downloads are two files, and the second must not
      // cancel the first the way a second search supersedes the first.
      mergeMap(({ target, documentId, fileName }) => {
        patchState(store, addPendingId(documentId));

        const url = store._api.build(
          `documents/${documentTargetPath(target)}/${ApiUrl.segment(target.id)}/${ApiUrl.segment(documentId)}`,
        );

        return store._http.get<DocumentDownloadDto>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (download) => {
              if (!save(download)) {
                store._toasts.show({
                  tone: 'error',
                  title: `${fileName} could not be downloaded`,
                  detail: 'The link the server returned is not a usable download link.',
                  traceLine: null,
                });
              }
            },
            error: (cause: unknown) => explain(toAppError(cause, { url }), fileName),
            finalize: () => patchState(store, removePendingId(documentId)),
          }),
        );
      }),
    );

    return {
      /**
       * Fetches a signed link and saves the file. Call this from a click, never from
       * a render.
       *
       * @param target The conversation or project the document hangs off.
       * @param documentId The persisted document.
       * @param fileName Used only in failure copy; the saved name comes from the link.
       */
      download(target: UploadTarget, documentId: string, fileName: string): void {
        _download({ target, documentId, fileName });
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
