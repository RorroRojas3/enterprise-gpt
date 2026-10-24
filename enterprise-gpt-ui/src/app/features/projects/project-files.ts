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
import { ProjectDocumentDto } from '@core/api/project.model';
import { DocumentDownloadStore } from '@core/documents/document-download-store';
import { canRetry } from '@core/errors/error-message';
import { ErrorPanel } from '@core/errors/error-panel';
import { SessionStore } from '@core/session/session-store';
import { SupportedExtensionsStore } from '@core/uploads/supported-extensions-store';
import { UploadStore } from '@core/uploads/upload-store';
import { Modal } from '@shared/dialog/modal';
import { DropOverlay } from '@shared/file-drop/drop-overlay';
import { FileDropTarget } from '@shared/file-drop/file-drop-target';
import { AttachmentChip } from '@shared/files/attachment-chip';
import { FILE_GLYPH_TEXT, fileGlyph } from '@shared/files/file-glyph';
import { formatBytes } from '@shared/formatting/bytes';
import { formatShortDate } from '@shared/formatting/relative-time';
import { Icon } from '@shared/icons/icon';
import { Skeleton } from '@shared/loading/skeleton';
import { ProjectDocumentsStore } from './project-documents-store';
import { ProjectStore } from './project-store';

/**
 * In-flight files are `UploadStore` chips, landed ones `ProjectDocumentsStore` rows. The bridge
 * between them lives on `ProjectDetail`, since an effect here would only run while mounted.
 */
@Component({
  selector: 'app-project-files',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AttachmentChip, DropOverlay, ErrorPanel, FileDropTarget, Icon, Modal, Skeleton],
  // Neither store is provided here: `ProjectDocumentsStore` is on the route so the tab count
  // survives a panel switch, and `UploadStore` on `ProjectDetail` so a tab switch does not cancel
  // a transfer. The composer uploads through its own instance.
  host: { class: 'd-flex flex-column gap-3 mw-prose' },
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
  protected readonly glyphText = FILE_GLYPH_TEXT;

  /** The document the removal confirmation names. Null keeps it closed. */
  protected readonly removeTarget = signal<ProjectDocumentDto | null>(null);
  protected readonly removeOpen = computed(() => this.removeTarget() !== null);

  protected readonly trackId = (document: ProjectDocumentDto): string => document.id;

  /** A field, not a template literal: exhaustive `checkNoChanges` sees a template array as new. */
  protected readonly skeletonRows = Array.from({ length: 3 });

  constructor() {
    // Loaded here as well as by the composer, which sits inside the header's `@if`: until the
    // list loads, `UploadStore.isSupported` accepts every file.
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
    // Cleared so re-picking the same file fires `change` again.
    input.value = '';
  }

  protected onFilesChosen(files: readonly File[]): void {
    if (files.length > 0) {
      this.uploads.attach(files);
    }
  }

  /** The signed link is requested at the click, never on render: it is a bearer credential. */
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
   * Fixes focus at close, not when the request settles: the optimistic row is already gone, and by
   * now the browser has finished its own `<dialog>` focus restoration.
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

  /** Browse, else the first row's action, else `#main-content`; only if focus fell through. */
  private _recoverFocus(): void {
    const active = this._document.activeElement;
    // No `!contains(active)` check: it would pull back a reader who deliberately tabbed away.
    const fellThrough = active === null || active === this._document.body || !active.isConnected;

    if (!fellThrough) {
      return;
    }

    const next =
      this._host.nativeElement.querySelector<HTMLElement>('[data-focus-next]') ??
      this._host.nativeElement.querySelector<HTMLElement>('[data-row-anchor]');

    if (next !== null) {
      next.focus();

      return;
    }

    this._document.getElementById('main-content')?.focus();
  }
}
