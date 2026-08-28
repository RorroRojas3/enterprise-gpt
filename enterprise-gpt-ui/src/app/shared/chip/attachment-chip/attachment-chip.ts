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
   * Carries both the in-flight state — the spinner is `aria-hidden`, so nothing else announces it —
   * and, for a generated file, that the assistant made it: this is the element that takes focus, and
   * the wrapper's `role="group"` name is announced on entry by some readers and not others.
   */
  protected readonly downloadLabel = computed(() => {
    const target = this.generated()
      ? `generated file ${this.attachment().fileName}`
      : this.attachment().fileName;

    return this.downloading() ? `Downloading ${target}` : `Download ${target}`;
  });

  /** Guards what `disabled` used to; see the template for why the attribute cannot be used here. */
  protected download(): void {
    if (!this.downloading()) {
      this.downloaded.emit(this.attachment().id);
    }
  }
}
