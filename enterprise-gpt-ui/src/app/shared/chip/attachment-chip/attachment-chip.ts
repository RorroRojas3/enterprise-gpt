import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Icon } from '@shared/icon/icon';
import { Attachment, fileGlyph } from './attachment.model';

/**
 * One attached file, in the composer or a project's files panel — frame `4l`.
 *
 * All five states render distinctly, and the differences are load-bearing rather than
 * decorative: an expired status (a 404 after the retention window) must not read as a
 * failure, because nothing failed and there is nothing to retry.
 */
@Component({
  selector: 'app-attachment-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './attachment-chip.html',
  styleUrl: './attachment-chip.scss',
})
export class AttachmentChip {
  readonly attachment = input.required<Attachment>();
  readonly removable = input<boolean>(true);

  readonly removed = output<string>();
  readonly retried = output<string>();

  protected readonly glyph = computed(() => fileGlyph(this.attachment().fileName));
  protected readonly removeLabel = computed(() => `Remove ${this.attachment().fileName}`);
  protected readonly retryLabel = computed(() => `Retry uploading ${this.attachment().fileName}`);
  protected readonly progressLabel = computed(() => `Uploading ${this.attachment().fileName}`);
}
