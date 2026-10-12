import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** Where a document came from. */
export type DocumentSource = 'uploaded' | 'generated';

/** Tells an uploaded file from a generated one in text, not colour alone. */
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
