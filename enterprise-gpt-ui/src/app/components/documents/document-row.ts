import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DocumentType, UserDocumentDto } from '@shared/models/api/document.model';
import { FILE_GLYPH_TEXT, fileGlyph } from '@shared/utils/documents/file-glyph';
import { formatBytes } from '@shared/utils/format/bytes';
import { formatShortDate } from '@shared/utils/format/relative-time';
import { CHAT_ROUTE } from '@core/navigation/route-paths';
import { DocumentDownloadStore } from '@state/document-download-store';
import { Icon } from '@shared/components/icon/icon';
import { DocumentSource, SourceBadge } from '@shared/components/source-badge/source-badge';

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
  host: {
    class: 'd-grid align-items-center column-gap-2 row-gap-1 px-3 py-2 py-md-0',
    // The stylesheet owns the grid track the missing conversation cell drops.
    '[class.document-row--no-conversation]': '!showConversation()',
  },
  templateUrl: './document-row.html',
  styleUrl: './document-row.scss',
})
export class DocumentRow {
  readonly document = input.required<UserDocumentDto>();

  /** Grouped rows pass `false`: their group header already names and links the conversation. */
  readonly showConversation = input(true);

  private readonly _downloads = inject(DocumentDownloadStore);

  protected readonly chatRoute = CHAT_ROUTE;

  protected readonly glyph = computed(() => fileGlyph(this.document().name));
  protected readonly glyphTone = computed(() => FILE_GLYPH_TEXT[this.glyph().tone]);

  /** The badge's own vocabulary, which `shared/models` cannot name without importing a component. */
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
