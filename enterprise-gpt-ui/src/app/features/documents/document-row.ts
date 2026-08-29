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
 * One row of the documents library (US-1002, frame `4j`) — the same anatomy as
 * `ProjectFiles`' frame-`4f` rows: glyph, name, size, date, then the row's controls.
 *
 * What this row adds over that panel's is the flat view's own two cells: the source
 * badge and the owning-conversation link. US-1003's grouped view names the
 * conversation in its group header instead, and passes {@link showConversation}
 * `false` — which also drops the cell's grid track, so the badge sits flush against
 * the download control rather than leaving a phantom column.
 *
 * The signed download link is requested at the click and never on render (US-804): it
 * is a bearer credential with a five-minute life, and a list that prefetched one per
 * row would mint a credential for every file the reader merely looked at.
 */
@Component({
  selector: 'app-document-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, RouterLink, SourceBadge],
  // The class carries the layout change so the stylesheet, not the template, owns the
  // grid: without the conversation cell the row also has one fewer track.
  host: { '[class.document-row--no-conversation]': '!showConversation()' },
  templateUrl: './document-row.html',
  styleUrl: './document-row.scss',
})
export class DocumentRow {
  readonly document = input.required<UserDocumentDto>();

  /**
   * Whether the owning-conversation link cell renders. The flat view keeps it (its
   * link column is US-1002's criterion); grouped rows pass `false`, because there the
   * group header already names and links the conversation (US-1003).
   */
  readonly showConversation = input(true);

  private readonly _downloads = inject(DocumentDownloadStore);

  protected readonly glyph = fileGlyph;
  protected readonly chatRoute = CHAT_ROUTE;

  /** The badge's own vocabulary, which `domain/` cannot name without reaching into `shared/`. */
  // Defaulted: the table is exhaustive against the union, not against a wire that could
  // add a member before the union does.
  protected readonly source = computed<DocumentSource>(
    () => BADGE_SOURCES[this.document().type] ?? 'uploaded',
  );

  protected sizeLabel(): string {
    return formatBytes(this.document().size);
  }

  protected dateLabel(): string {
    return formatShortDate(this.document().dateCreated, true);
  }

  /** Fetches a signed link and saves the file — US-804's flow, unchanged. */
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
