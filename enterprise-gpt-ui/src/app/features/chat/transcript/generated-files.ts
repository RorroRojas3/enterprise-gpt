import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MessageAttachmentDto } from '@domain/api/conversation';
import { formatBytes } from '@domain/format/bytes';
import { DocumentDownloadStore } from '@core/documents/document-download-store';
import { AttachmentChip } from '@shared/chip/attachment-chip/attachment-chip';

/** The files an assistant message produced, under its answer. */
@Component({
  selector: 'app-generated-files',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AttachmentChip],
  templateUrl: './generated-files.html',
  styleUrl: './generated-files.scss',
})
export class GeneratedFiles {
  readonly attachments = input.required<readonly MessageAttachmentDto[]>();
  /** The conversation the download route reads ownership from. */
  readonly conversationId = input.required<string | null>();

  private readonly _downloads = inject(DocumentDownloadStore);

  protected readonly rows = computed(() =>
    this.attachments().map((file) => ({
      id: file.id,
      fileName: file.name,
      sizeLabel: formatBytes(file.size),
      state: { kind: 'ready' } as const,
    })),
  );

  /** Whether a download can be addressed at all. A chip that cannot act does not offer to. */
  protected readonly downloadable = computed(() => this.conversationId() !== null);

  /** Whether this file's own link is being minted. Keyed by document id, so one chip spins alone. */
  protected downloading(documentId: string): boolean {
    return this._downloads.isRowPending(documentId);
  }

  protected download(documentId: string): void {
    const conversationId = this.conversationId();
    const file = this.attachments().find((candidate) => candidate.id === documentId);

    if (conversationId === null || file === undefined) {
      return;
    }

    // Asked for at the moment of the click, never on render: the link is a bearer credential
    // with a lifetime in minutes, and prefetching one would spend it before it is wanted.
    this._downloads.download({ kind: 'conversation', id: conversationId }, documentId, file.name);
  }
}
