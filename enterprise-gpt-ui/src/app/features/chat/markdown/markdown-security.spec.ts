import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MarkdownComponent, MarkdownService } from 'ngx-markdown';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CHAT_ALLOWED_TAGS, provideChatMarkdown, sanitizeChatMarkdown } from './markdown-providers';

/**
 * US-601. The payloads here are the ones the story names, driven through the
 * real renderer rather than through a stub: the point is that *this* pipeline —
 * the marked renderer override, then DOMPurify with the app's profile — is what
 * refuses them, so a spec that mocked either layer would prove nothing.
 */
@Component({
  selector: 'app-markdown-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownComponent],
  template: `<markdown [data]="source()" (ready)="rendered()" />`,
})
class MarkdownHost {
  readonly source = signal('');

  private _resolve: (() => void) | null = null;

  /**
   * Resolves when the renderer has actually written its output.
   *
   * Counting microtasks by hand would couple these specs to how many times
   * ngx-markdown happens to await internally — and every assertion here is a
   * *negative* one, so reading the DOM a beat early would pass for the wrong
   * reason and quietly stop testing anything.
   */
  nextRender(): Promise<void> {
    return new Promise<void>((resolve) => {
      this._resolve = resolve;
    });
  }

  protected rendered(): void {
    this._resolve?.();
    this._resolve = null;
  }
}

describe('chat markdown rendering (US-601)', () => {
  let fixture: ComponentFixture<MarkdownHost>;
  let host: HTMLElement;
  let alertSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    alertSpy = vi.fn();
    vi.stubGlobal('alert', alertSpy);

    TestBed.configureTestingModule({ providers: [provideChatMarkdown()] });
    fixture = TestBed.createComponent(MarkdownHost);
    host = fixture.nativeElement as HTMLElement;
    document.body.append(host);
  });

  afterEach(() => {
    host.remove();
    vi.unstubAllGlobals();
  });

  async function render(markdown: string): Promise<HTMLElement> {
    const rendered = fixture.componentInstance.nextRender();
    fixture.componentInstance.source.set(markdown);
    await fixture.whenStable();
    await rendered;

    return host.querySelector('markdown') as HTMLElement;
  }

  describe('the payloads the story names', () => {
    it('renders an onerror image as nothing executable', async () => {
      const element = await render('<img src=x onerror=alert(1)>');

      expect(element.querySelector('img')).toBeNull();
      expect(element.innerHTML).not.toContain('onerror');
      expect(alertSpy).not.toHaveBeenCalled();
    });

    it('renders a script tag as nothing executable', async () => {
      const element = await render('<script>alert(1)</script>');

      expect(element.querySelector('script')).toBeNull();
      expect(element.innerHTML).not.toContain('alert(1)');
      expect(alertSpy).not.toHaveBeenCalled();
    });

    it('strips a javascript: href while keeping the link text', async () => {
      const element = await render('[click me](javascript:alert(1))');

      const anchor = element.querySelector('a');
      expect(anchor).not.toBeNull();
      expect(anchor?.hasAttribute('href')).toBe(false);
      expect(anchor?.textContent).toBe('click me');
      expect(alertSpy).not.toHaveBeenCalled();
    });

    it('leaves ordinary markdown intact', async () => {
      const element = await render('A **bold** claim and a [link](https://example.com).');

      expect(element.querySelector('strong')?.textContent).toBe('bold');
      expect(element.querySelector('a')?.getAttribute('href')).toBe('https://example.com');
    });
  });

  /**
   * The parser-level layer, proved without the sanitizer. `disableSanitizer`
   * turns DOMPurify off for this one call, so anything still missing from the
   * output was dropped by the renderer override rather than filtered afterwards.
   */
  describe('raw HTML suppression at the parser', () => {
    it('drops raw html tokens with the sanitizer disabled', async () => {
      const parsed = await TestBed.inject(MarkdownService).parse(
        'Inline <em>raw</em> text.\n\n<div class="block">block</div>',
        { disableSanitizer: true },
      );

      expect(parsed).not.toContain('<em>');
      expect(parsed).not.toContain('<div');
      expect(parsed).toContain('Inline');
      expect(parsed).toContain('raw');
    });
  });

  describe('the DOMPurify profile', () => {
    it('drops data attributes but keeps the class marked emits', () => {
      expect(sanitizeChatMarkdown('<p data-foo="bar" class="keep">x</p>')).toBe(
        '<p class="keep">x</p>',
      );
    });

    it('drops images, so a model answer cannot reach a third-party host', () => {
      expect(sanitizeChatMarkdown('<p><img src="https://evil.example/x.png"></p>')).toBe('<p></p>');
    });

    it('keeps the markup marked emits for code, tables and task lists', () => {
      const code = '<pre><code class="language-ts">const a = 1;</code></pre>';
      expect(sanitizeChatMarkdown(code)).toBe(code);

      const table = '<table><thead><tr><th align="left">a</th></tr></thead></table>';
      expect(sanitizeChatMarkdown(table)).toBe(table);

      const task = '<ul><li><input checked="" disabled="" type="checkbox"> done</li></ul>';
      expect(sanitizeChatMarkdown(task)).toBe(task);
    });

    it('does not allow span, because Prism adds its own after sanitizing', () => {
      expect(CHAT_ALLOWED_TAGS).not.toContain('span');
      expect(sanitizeChatMarkdown('<span class="token">x</span>')).toBe('x');
    });

    it('keeps US-603’s chrome, and still strips what a button could carry', () => {
      const chrome =
        '<div class="md-code"><button class="md-code__copy" type="button">Copy</button></div>';
      expect(sanitizeChatMarkdown(chrome)).toBe(chrome);

      // `div` and `button` are allowed as *elements*; nothing about that lets an
      // event handler or a style through with them.
      expect(sanitizeChatMarkdown('<button onclick="alert(1)" style="x">Copy</button>')).toBe(
        '<button>Copy</button>',
      );
    });
  });

  describe('the renderer overrides', () => {
    it('names task-list checkboxes rather than hiding their state', async () => {
      const element = await render('- [x] done\n- [ ] todo');

      const boxes = [...element.querySelectorAll('input')];
      expect(boxes).toHaveLength(2);
      for (const box of boxes) {
        expect(box.hasAttribute('disabled')).toBe(true);
      }
      // The box is the only thing carrying done-ness — "done" and "todo" read
      // the same — so hiding it would take the state away from a screen reader.
      expect(boxes[0]?.getAttribute('aria-label')).toBe('Completed');
      expect(boxes[0]?.hasAttribute('checked')).toBe(true);
      expect(boxes[1]?.getAttribute('aria-label')).toBe('Not completed');
      expect(boxes[1]?.hasAttribute('checked')).toBe(false);
    });

    it('shifts answer headings one level below the conversation title', async () => {
      const element = await render('# Top\n\n## Second\n\n###### Deepest');

      // The page's h1 is the conversation name. Shifting by one lands directly
      // beneath it: jumping straight to h3 would itself be a heading-order fault.
      expect(element.querySelector('h1')).toBeNull();
      expect(element.querySelector('h2')?.textContent).toBe('Top');
      expect(element.querySelector('h3')?.textContent).toBe('Second');
      // Clamped rather than overflowing into a tag that does not exist.
      expect(element.querySelector('h6')?.textContent).toBe('Deepest');
    });

    it('suppresses raw HTML inside a heading, not just in prose', async () => {
      // Headings go through their own renderer, which has to reach the parser
      // carrying these options rather than build a default one.
      const element = await render('## <script>alert(1)</script> heading');

      // The tags are gone and what is left is inert text, exactly as in prose —
      // before this the heading renderer built its own parser with marked's
      // defaults, and the script tag survived the parser layer untouched.
      expect(element.querySelector('script')).toBeNull();
      expect(element.innerHTML).not.toContain('<script');
      expect(element.querySelector('h3')?.textContent).toContain('heading');
      expect(alertSpy).not.toHaveBeenCalled();
    });

    it('escapes alt text rather than letting it re-enter as markup', async () => {
      const element = await render('![<em>bold alt</em> & more](p.png)');

      expect(element.querySelector('em')).toBeNull();
      expect(element.textContent).toContain('<em>bold alt</em> & more');
    });

    it('keeps an image’s alt text instead of leaving a hole', async () => {
      const element = await render('![a wiring diagram](https://example.com/a.png)');

      expect(element.querySelector('img')).toBeNull();
      expect(element.textContent).toContain('a wiring diagram');
    });
  });

  /**
   * US-603. The chrome is part of the rendered output, which is what lets a
   * single delegated listener serve it; these specs pin the markup that listener
   * reads and the info string it must never turn into an attribute.
   */
  describe('the code-block chrome', () => {
    it('wraps a fenced block in the head bar, naming the language', async () => {
      const element = await render('```ts\nconst a = 1;\n```');

      const wrapper = element.querySelector('.md-code');
      expect(wrapper?.querySelector('.md-code__lang')?.textContent).toBe('ts');
      expect(wrapper?.querySelector('.md-code__copy')?.textContent).toBe('Copy');
      // Prism's canonical markup: the marker on the `pre` as well as the `code`.
      expect(wrapper?.querySelector('pre')?.className).toContain('language-ts');
      expect(wrapper?.querySelector('pre > code')?.textContent).toBe('const a = 1;\n');
    });

    it('offers the control on an unlabelled block, with no language to name', async () => {
      const element = await render('```\nplain text\n```');

      expect(element.querySelector('.md-code__lang')).toBeNull();
      expect(element.querySelector('.md-code__copy')).not.toBeNull();
    });

    it('escapes the block body rather than parsing it', async () => {
      const element = await render('```html\n<script>alert(1)</script> & <b>x</b>\n```');

      expect(element.querySelector('script')).toBeNull();
      expect(element.querySelector('pre > code')?.textContent).toBe(
        '<script>alert(1)</script> & <b>x</b>\n',
      );
      expect(alertSpy).not.toHaveBeenCalled();
    });

    it('names each control, so six code blocks are not six identical buttons', async () => {
      const element = await render('```ts\nconst a = 1;\n```\n\n```\nplain\n```');

      const names = [...element.querySelectorAll('.md-code__copy')].map((button) =>
        button.getAttribute('aria-label'),
      );
      expect(names).toEqual(['Copy ts code block', 'Copy code block']);
    });

    it('refuses raw chrome in the source, so the delegated listener cannot be aimed', async () => {
      // `div` and `button` are in the profile for `renderer.code`'s sake, which
      // is the one place the two layers stop being independent: if the parser
      // layer ever let raw HTML past, model text could mint a wrapper the
      // transcript's copy listener would serve. This is that regression's alarm.
      const element = await render(
        '<div class="md-code"><button class="md-code__copy">Copy</button></div>',
      );

      expect(element.querySelector('.md-code__copy')).toBeNull();
      expect(element.querySelector('button')).toBeNull();
    });

    it('refuses an info string that is not a language, rather than escaping it', async () => {
      const element = await render('```ts"onload=alert(1)\nconst a = 1;\n```');

      const pre = element.querySelector('pre');
      // The character set stops at the quote, so nothing can break out of the
      // attribute in the first place — and no stray attribute survives either.
      expect(pre?.className).toBe('language-ts');
      expect(pre?.getAttribute('onload')).toBeNull();
      expect(alertSpy).not.toHaveBeenCalled();
    });
  });
});
