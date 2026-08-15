import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ProjectDocumentDto } from '@domain/api/project';
import { formatBytes } from '@domain/format/bytes';
import { formatShortDate } from '@domain/format/relative-time';
import { DocumentDownloadStore } from '@core/documents/document-download-store';
import { SupportedExtensionsStore } from '@core/documents/supported-extensions-store';
import { UploadStore } from '@core/documents/upload-store';
import { canRetry } from '@core/errors/error-message';
import { SessionStore } from '@core/session/session-store';
import { AttachmentChip } from '@shared/chip/attachment-chip/attachment-chip';
import { fileGlyph } from '@shared/chip/attachment-chip/attachment.model';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { Icon } from '@shared/icon/icon';
import { Modal } from '@shared/overlay/modal/modal';
import { DropOverlay } from '@shared/upload/drop-overlay';
import { FileDropTarget } from '@shared/upload/file-drop-target';
import { ProjectStore } from '../project-store';
import { ProjectDocumentsStore } from './project-documents-store';

/**
 * A project's files (US-905, frame `4f`).
 *
 * Two stores, and the split is EP-8's: `UploadStore` owns files that are *in flight*
 * and renders them as chips, `ProjectDocumentsStore` owns files that have *landed* and
 * renders them as rows. Everything under the upload store is already target-agnostic —
 * `UploadTarget` is a discriminated `conversation | project` pair, and the transport,
 * the staged poll and the download all route through it — so this panel adds no upload
 * code at all. It binds `{ kind: 'project', id }` and reuses the lot.
 *
 * The bridge between the two — a chip that reaches `ready` becoming a row — lives on
 * `ProjectDetail`, not here: `UploadStore` is provided there so the composer can upload
 * from any tab, and an effect on this panel would only run while the panel is mounted.
 */
@Component({
  selector: 'app-project-files',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AttachmentChip, DropOverlay, ErrorPanel, FileDropTarget, Icon, Modal, Skeleton],
  // Neither store is provided here. `ProjectDocumentsStore` is on the route, so the tab
  // strip's count survives a switch to another panel; `UploadStore` is on
  // `ProjectDetail`, so a file dropped on the composer and one dropped here are the
  // same set of chips and a tab switch mid-transfer does not cancel it.
  templateUrl: './project-files.html',
  styleUrl: './project-files.scss',
})
export class ProjectFiles {
  protected readonly project = inject(ProjectStore);
  protected readonly documents = inject(ProjectDocumentsStore);
  protected readonly uploads = inject(UploadStore);
  protected readonly session = inject(SessionStore);
  protected readonly extensions = inject(SupportedExtensionsStore);

  private readonly _downloads = inject(DocumentDownloadStore);
  private readonly _document = inject(DOCUMENT);
  private readonly _injector = inject(Injector);
  private readonly _host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly _filePicker = viewChild<ElementRef<HTMLInputElement>>('filePicker');

  protected readonly canRetry = canRetry;
  protected readonly glyph = fileGlyph;

  /** The document the removal confirmation names. Null keeps it closed. */
  protected readonly removeTarget = signal<ProjectDocumentDto | null>(null);
  protected readonly removeOpen = computed(() => this.removeTarget() !== null);

  protected readonly trackId = (document: ProjectDocumentDto): string => document.id;

  /**
   * A field, not a template literal: `provideCheckNoChangesConfig({ exhaustive: true })`
   * compares bindings between passes, and an array allocated in the template is a new
   * value every check.
   */
  protected readonly skeletonRows = Array.from({ length: 3 });

  constructor() {
    // The picker's `accept` list is deployment-shaped and memoised for the session, so
    // the first surface to render pays for it and nothing else does. Gated on the
    // permission for the same reason the composer gates it. Kept here even though the
    // composer asks too: the composer sits inside the header's `@if`, so while the
    // project's own request is pending or failed this panel would otherwise render with
    // an empty `accept` — and `UploadStore.isSupported` answers `true` for everything
    // while the list is unloaded, which makes US-805's client-side refusal inert.
    effect(() => {
      if (this.session.canUploadFiles()) {
        void this.extensions.ensureLoaded();
      }
    });
  }

  protected sizeLabel(document: ProjectDocumentDto): string {
    return formatBytes(document.size);
  }

  protected dateLabel(document: ProjectDocumentDto): string {
    return formatShortDate(document.dateCreated, true);
  }

  protected openFilePicker(): void {
    this._filePicker()?.nativeElement.click();
  }

  protected onFileInputChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.onFilesChosen(Array.from(input.files ?? []));
    // Cleared so re-picking the same file fires `change` again — without this,
    // removing a chip and choosing the identical file is a silent no-op.
    input.value = '';
  }

  protected onFilesChosen(files: readonly File[]): void {
    if (files.length > 0) {
      this.uploads.attach(files);
    }
  }

  /**
   * Fetches a document back (US-804's flow, unchanged).
   *
   * The signed link is requested here, at the click, and never on render: it is a
   * bearer credential with a five-minute life, and a list that prefetched one per row
   * would mint a credential for every file the reader merely looked at.
   */
  protected download(document: ProjectDocumentDto): void {
    const projectId = this.project.projectId();
    if (projectId === null) {
      return;
    }

    this._downloads.download({ kind: 'project', id: projectId }, document.id, document.name);
  }

  protected isDownloading(document: ProjectDocumentDto): boolean {
    return this._downloads.isRowPending(document.id);
  }

  protected confirmRemove(): void {
    const target = this.removeTarget();
    this.removeTarget.set(null);

    if (target !== null) {
      this._confirming = true;
      this.documents.remove(target);
    }
  }

  /**
   * The modal closed. Only a confirmed removal runs the focus fixup.
   *
   * Driven from here rather than from the request settling, which is a whole round trip
   * later: the removal is optimistic, so the row and its Remove button are gone in the
   * next frame, and waiting would fire the fixup on a *failed* removal too — the
   * pending id clears identically on a 204 and a 500. Closing is also when the browser
   * has finished its own `<dialog>` focus restoration, so this runs after it rather
   * than against it.
   */
  protected onRemoveClosed(): void {
    this.removeTarget.set(null);

    if (!this._confirming) {
      return;
    }
    this._confirming = false;

    afterNextRender(() => this._recoverFocus(), { injector: this._injector });
  }

  /** Set on confirm, so only that close path runs the focus fallback. */
  private _confirming = false;

  /**
   * Puts focus somewhere real after a confirmed removal.
   *
   * The browse control if there is one, the first surviving row's download button
   * otherwise, and `#main-content` as the last resort — the one always-present
   * programmatic target, which the delete-project dialog falls back to for the same
   * reason. Only when focus actually fell through.
   */
  private _recoverFocus(): void {
    const active = this._document.activeElement;
    // No `!contains(active)` clause. The panel — not a closed dialog — is the host
    // here, so testing for focus *outside* it would pull back a reader who had
    // deliberately tabbed away, which is the opposite of what this is for.
    const fellThrough = active === null || active === this._document.body || !active.isConnected;

    if (!fellThrough) {
      return;
    }

    const next =
      this._host.nativeElement.querySelector<HTMLElement>('.files__browse') ??
      this._host.nativeElement.querySelector<HTMLElement>('.files__action');

    if (next !== null) {
      next.focus();

      return;
    }

    this._document.getElementById('main-content')?.focus();
  }
}
