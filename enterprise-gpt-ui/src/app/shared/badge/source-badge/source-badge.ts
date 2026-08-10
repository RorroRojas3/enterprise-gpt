import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** Where a document came from — frame `4j`'s documents library. */
export type DocumentSource = 'uploaded' | 'generated';

/**
 * Distinguishes a file the user uploaded from one the assistant produced.
 *
 * Text, not colour alone: "Generated" reads the same to someone who cannot tell the
 * accent outline from the neutral one.
 */
@Component({
  selector: 'app-source-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="source-badge" [class]="'source-badge--' + source()">{{ label() }}</span>`,
  styleUrl: './source-badge.scss',
})
export class SourceBadge {
  readonly source = input.required<DocumentSource>();

  protected readonly label = computed(() =>
    this.source() === 'generated' ? 'Generated' : 'Uploaded',
  );
}
