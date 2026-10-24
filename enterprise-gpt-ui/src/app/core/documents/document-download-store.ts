import { HttpClient } from '@angular/common/http';
import { DOCUMENT, inject } from '@angular/core';
import { tapResponse } from '@ngrx/operators';
import { patchState, signalStore, withMethods, withProps } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { mergeMap, takeUntil } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { DocumentDownloadDto, UploadTarget } from '@core/api/document.model';
import { addPendingId, removePendingId, withPendingIds } from '@core/api/with-pending-ids';
import { AppError } from '@core/errors/app-error';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';
import { saveWithAnchor } from '@shared/downloads/anchor-download';
import { documentTargetPath } from './document-target-path';

/**
 * Fetches a document's signed link on click, never before: it is a bearer credential for about
 * five minutes. The URL never reaches state, storage, the router or a log, and is followed by
 * an anchor rather than `HttpClient`, so it never carries the app's `Authorization` header.
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
    /** Checks the scheme here, not in `saveWithAnchor`: only a server-chosen URL needs it. */
    function save(download: DocumentDownloadDto): boolean {
      // A direct `href` bypasses `DomSanitizer`, so a `javascript:` URL would run on click;
      // defence in depth, since the value is server-issued.
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
          // A deployment fact, not a failure of this file, and not retryable.
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
      // mergeMap: a second download is a second file and must not cancel the first.
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
      /** Call from a click, never a render. `fileName` is only for failure copy. */
      download(target: UploadTarget, documentId: string, fileName: string): void {
        _download({ target, documentId, fileName });
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
