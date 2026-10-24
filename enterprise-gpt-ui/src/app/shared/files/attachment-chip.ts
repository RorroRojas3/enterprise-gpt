import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { SourceBadge } from '@shared/badges/source-badge';
import { Icon } from '@shared/icons/icon';
import { IconName } from '@shared/icons/icon-names';
import { Attachment, AttachmentRemoveAction } from './attachment.model';
import { FILE_GLYPH_TEXT, fileGlyph } from './file-glyph';

/**
 * Every state renders distinctly: an expired status (a 404 after the retention window) must not
 * read as a failure, because nothing failed and there is nothing to retry.
 */
@Component({
  selector: 'app-attachment-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, SourceBadge],
  templateUrl: './attachment-chip.html',
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

  /** A generated file leads with its type: the upload tick answers a question nobody asked. */
  protected readonly lead = computed<{ icon: IconName; tone: string }>(() => {
    const kind = this.attachment().state.kind;
    if (!this.generated()) {
      if (kind === 'ready') return { icon: 'bi-check-circle-fill', tone: 'text-success-emphasis' };
      if (kind === 'failed' || kind === 'unsupported') {
        return { icon: 'bi-x-circle-fill', tone: 'text-danger' };
      }
      if (kind === 'unknown') return { icon: 'bi-question-circle', tone: 'text-body-secondary' };
    }
    const glyph = fileGlyph(this.attachment().fileName);
    return { icon: glyph.icon, tone: FILE_GLYPH_TEXT[glyph.tone] };
  });
  protected readonly border = computed(() => {
    if (this.generated()) return 'border-info-subtle';
    const kind = this.attachment().state.kind;
    return kind === 'failed' || kind === 'unsupported' ? 'border-danger-subtle' : '';
  });
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
