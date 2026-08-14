import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture } from '@angular/core/testing';
import { MarkdownComponent } from 'ngx-markdown';

/**
 * A bare host for driving one source string through the real chat markdown
 * pipeline — the marked renderer overrides, then DOMPurify with the app's
 * profile — rather than through a stub.
 *
 * Every spec that reads rendered output needs the same thing and needs it to be
 * exactly right, which is why it is shared: the renderer writes its output a
 * beat *after* change detection returns, and reading the DOM early would pass a
 * negative assertion for the wrong reason.
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

  /**
   * Resolves when the renderer has actually written its output.
   *
   * Counting microtasks by hand would couple callers to how many times
   * ngx-markdown happens to await internally.
   */
  nextRender(): Promise<void> {
    if (this._resolve !== null) {
      // `ready` fires off a floating promise, so a second caller waiting on the
      // same slot would silently be handed the *previous* render's completion.
      // Failing here turns that into a red test rather than a stale assertion.
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
