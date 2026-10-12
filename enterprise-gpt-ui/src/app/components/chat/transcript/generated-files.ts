import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MessageAttachmentDto } from '@shared/models/api/conversation.model';
import { formatBytes } from '@shared/utils/format/bytes';
import { DocumentDownloadStore } from '@state/document-download-store';
import { AttachmentChip } from '@shared/components/attachment-chip/attachment-chip';

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

  /** A chip that cannot address a download does not offer one. */
  protected readonly downloadable = computed(() => this.conversationId() !== null);

  /** Keyed by document id, so one chip spins alone. */
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
