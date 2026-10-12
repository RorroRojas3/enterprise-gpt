import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Attachment, AttachmentRemoveAction } from '@shared/models/attachment.model';
import { fileGlyph } from '@shared/utils/documents/file-glyph';
import { Icon } from '@shared/components/icon/icon';
import { SourceBadge } from '@shared/components/source-badge/source-badge';

/**
 * Every state renders distinctly: an expired status (a 404 after the retention window) must not
 * read as a failure, because nothing failed and there is nothing to retry.
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
  /** Whether the original file can be fetched back. */
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
   * Carries the in-flight state, since the spinner is `aria-hidden`, and a generated file's origin,
   * since this takes focus and not every reader announces the group's name on entry.
   */
  protected readonly downloadLabel = computed(() => {
    const target = this.generated()
      ? `generated file ${this.attachment().fileName}`
      : this.attachment().fileName;

    return this.downloading() ? `Downloading ${target}` : `Download ${target}`;
  });

  /** The guard `disabled` would give; the template says why the attribute cannot be used. */
  protected download(): void {
    if (!this.downloading()) {
      this.downloaded.emit(this.attachment().id);
    }
  }
}
