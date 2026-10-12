import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DocumentType, UserDocumentDto } from '@domain/api/document';
import { formatBytes } from '@domain/format/bytes';
import { formatShortDate } from '@domain/format/relative-time';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { DocumentDownloadStore } from '@core/documents/document-download-store';
import { DocumentSource, SourceBadge } from '@shared/badge/source-badge/source-badge';
import { fileGlyph } from '@shared/chip/attachment-chip/attachment.model';
import { Icon } from '@shared/icon/icon';

const BADGE_SOURCES = {
  Uploaded: 'uploaded',
  Generated: 'generated',
} as const satisfies Record<DocumentType, DocumentSource>;

/**
 * The signed download link is requested on click, never on render: it is a five-minute bearer
 * credential, and prefetching would mint one for every row merely looked at.
 */
@Component({
  selector: 'app-document-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, RouterLink, SourceBadge],
  // A host class, so the stylesheet owns the grid track the missing conversation cell drops.
  host: { '[class.document-row--no-conversation]': '!showConversation()' },
  templateUrl: './document-row.html',
  styleUrl: './document-row.scss',
})
export class DocumentRow {
  readonly document = input.required<UserDocumentDto>();

  /** Grouped rows pass `false`: their group header already names and links the conversation. */
  readonly showConversation = input(true);

  private readonly _downloads = inject(DocumentDownloadStore);

  protected readonly glyph = fileGlyph;
  protected readonly chatRoute = CHAT_ROUTE;

  /** The badge's own vocabulary, which `domain/` cannot name without reaching into `shared/`. */
  // Defaulted: the wire could add a type before the union does.
  protected readonly source = computed<DocumentSource>(
    () => BADGE_SOURCES[this.document().type] ?? 'uploaded',
  );

  protected sizeLabel(): string {
    return formatBytes(this.document().size);
  }

  protected dateLabel(): string {
    return formatShortDate(this.document().dateCreated, true);
  }

  /** Fetches a signed link and saves the file. */
  protected download(): void {
    const document = this.document();

    this._downloads.download(
      { kind: 'conversation', id: document.conversationId },
      document.id,
      document.name,
    );
  }

  protected isDownloading(): boolean {
    return this._downloads.isRowPending(this.document().id);
  }
}
