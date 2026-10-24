import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** Where a document came from. */
export type DocumentSource = 'uploaded' | 'generated';

/** Tells an uploaded file from a generated one in text, not colour alone. */
@Component({
  selector: 'app-source-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="badge rounded-pill border fw-semibold" [class]="tone()">{{ label() }}</span>
  `,
})
export class SourceBadge {
  readonly source = input.required<DocumentSource>();

  protected readonly label = computed(() =>
    this.source() === 'generated' ? 'Generated' : 'Uploaded',
  );

  // Primary text, not info: light info is 2.74:1 on the surface. The border keeps the accent.
  protected readonly tone = computed(() =>
    this.source() === 'generated' ? 'text-primary border-info-subtle' : 'text-body-secondary',
  );
}
