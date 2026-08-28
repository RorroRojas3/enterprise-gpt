import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Icon } from '@shared/icon/icon';
import { SourceBadge } from '@shared/badge/source-badge/source-badge';
import { Attachment, AttachmentRemoveAction } from '@domain/documents/attachment';
import { fileGlyph } from './attachment.model';

/**
 * One attached file, in the composer or a project's files panel — frame `4l`.
 *
 * Every state renders distinctly, and the differences are load-bearing rather than
 * decorative: an expired status (a 404 after the retention window) must not read as a
 * failure, because nothing failed and there is nothing to retry.
 */
@Component({
  selector: 'app-attachment-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, SourceBadge],
  templateUrl: './attachment-chip.html',
  styleUrl: './attachment-chip.scss',
})
export class AttachmentChip {
  readonly attachment = input.required<Attachment>();
  readonly removable = input<boolean>(true);
  /** Whether the `×` still cancels something. See {@link AttachmentRemoveAction}. */
  readonly removeAction = input<AttachmentRemoveAction>('cancel');
  /** Whether the original file can be fetched back (US-804). */
  readonly downloadable = input<boolean>(false);
  /** Whether a download request for this file is already in flight. */
  readonly downloading = input<boolean>(false);
  /**
   * Not a sixth {@link Attachment} state: a generated file is outside the upload lifecycle
   * those describe, and it is always finished.
   */
  readonly generated = input<boolean>(false);

  readonly removed = output<string>();
  readonly retried = output<string>();
  readonly downloaded = output<string>();

  protected readonly glyph = computed(() => fileGlyph(this.attachment().fileName));
  /** The chip's own accessible name, so the distinction survives without sight of the badge. */
  protected readonly generatedLabel = computed(
    () => `Generated file, ${this.attachment().fileName}`,
  );
  protected readonly removeLabel = computed(() =>
    this.removeAction() === 'cancel'
      ? `Remove ${this.attachment().fileName}`
      : `Dismiss ${this.attachment().fileName}`,
  );
  protected readonly retryLabel = computed(() => `Retry uploading ${this.attachment().fileName}`);
  protected readonly progressLabel = computed(() => `Uploading ${this.attachment().fileName}`);
  /**
   * The in-flight state is otherwise visual only: the spinner is `aria-hidden` and
   * `disabled` takes the control out of the tab order, so the label has to carry it.
   */
  protected readonly downloadLabel = computed(() =>
    this.downloading()
      ? `Downloading ${this.attachment().fileName}`
      : `Download ${this.attachment().fileName}`,
  );
}
