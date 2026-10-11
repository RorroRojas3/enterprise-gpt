import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture } from '@angular/core/testing';
import { MarkdownComponent } from 'ngx-markdown';

/**
 * Drives a source string through the real markdown pipeline. Shared because the renderer writes
 * after change detection returns, and an early read passes negative assertions wrongly.
 */
@Component({
  selector: 'app-markdown-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownComponent],
  template: `<markdown [data]="source()" (ready)="rendered()" />`,
})
export class MarkdownHost {
  readonly source = signal('');

  private _resolve: (() => void) | null = null;

  /** Resolves once the renderer has written, without counting ngx-markdown's internal awaits. */
  nextRender(): Promise<void> {
    if (this._resolve !== null) {
      // A second waiter on this slot would be handed the previous render's completion.
      throw new Error('A render is already being awaited on this host.');
    }

    return new Promise<void>((resolve) => {
      this._resolve = resolve;
    });
  }

  protected rendered(): void {
    this._resolve?.();
    this._resolve = null;
  }
}

/** Renders `markdown` through `fixture` and hands back the `<markdown>` element. */
export async function renderMarkdown(
  fixture: ComponentFixture<MarkdownHost>,
  markdown: string,
): Promise<HTMLElement> {
  const rendered = fixture.componentInstance.nextRender();
  fixture.componentInstance.source.set(markdown);
  await fixture.whenStable();
  await rendered;

  return (fixture.nativeElement as HTMLElement).querySelector('markdown') as HTMLElement;
}
