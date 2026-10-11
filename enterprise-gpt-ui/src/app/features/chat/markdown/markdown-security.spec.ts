import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MarkdownService } from 'ngx-markdown';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MarkdownHost, renderMarkdown } from '@testing/markdown-host';
import { CHAT_ALLOWED_TAGS, provideChatMarkdown, sanitizeChatMarkdown } from './markdown-providers';

/** Driven through the real renderer and DOMPurify profile: mocking either layer proves nothing. */
describe('chat markdown rendering', () => {
  let fixture: ComponentFixture<MarkdownHost>;
  let alertSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    alertSpy = vi.fn();
    vi.stubGlobal('alert', alertSpy);

    TestBed.configureTestingModule({ providers: [provideChatMarkdown()] });
    fixture = TestBed.createComponent(MarkdownHost);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  async function render(markdown: string): Promise<HTMLElement> {
    return renderMarkdown(fixture, markdown);
  }

  describe('well-known script-injection payloads', () => {
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

  /** With DOMPurify off for the call, anything missing was dropped by the renderer override. */
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

    it('keeps the code-block copy chrome, and still strips what a button could carry', () => {
      const chrome =
        '<div class="md-code"><button class="md-code__copy" type="button">Copy</button></div>';
      expect(sanitizeChatMarkdown(chrome)).toBe(chrome);

      // Allowed as elements only; handlers and styles still go.
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
      // The box alone carries done-ness; the item text reads the same either way.
      expect(boxes[0]?.getAttribute('aria-label')).toBe('Completed');
      expect(boxes[0]?.hasAttribute('checked')).toBe(true);
      expect(boxes[1]?.getAttribute('aria-label')).toBe('Not completed');
      expect(boxes[1]?.hasAttribute('checked')).toBe(false);
    });

    it('shifts answer headings one level below the conversation title', async () => {
      const element = await render('# Top\n\n## Second\n\n###### Deepest');

      // The page's h1 is the conversation name; jumping to h3 would be a heading-order fault.
      expect(element.querySelector('h1')).toBeNull();
      expect(element.querySelector('h2')?.textContent).toBe('Top');
      expect(element.querySelector('h3')?.textContent).toBe('Second');
      // Clamped rather than overflowing into a tag that does not exist.
      expect(element.querySelector('h6')?.textContent).toBe('Deepest');
    });

    it('suppresses raw HTML inside a heading, not just in prose', async () => {
      // Headings have their own renderer, which must reach the parser carrying these options.
      const element = await render('## <script>alert(1)</script> heading');

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

  /** One delegated listener serves the chrome, so these pin the markup it reads. */
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
      // `div` and `button` rest on the parser layer: if raw HTML got past it, model text could
      // mint a wrapper the transcript's copy listener would serve.
      const element = await render(
        '<div class="md-code"><button class="md-code__copy">Copy</button></div>',
      );

      expect(element.querySelector('.md-code__copy')).toBeNull();
      expect(element.querySelector('button')).toBeNull();
    });

    it('makes an overflowing block reachable by keyboard', async () => {
      const element = await render('```ts\nconst a = 1;\n```\n\n```\nplain\n```');

      const blocks = [...element.querySelectorAll('pre')];
      expect(blocks.map((pre) => pre.getAttribute('tabindex'))).toEqual(['0', '0']);

      // Unnamed on purpose: ARIA 1.2 prohibits naming `<pre>`'s generic role, and the Copy
      // control carries the name.
      expect(blocks.map((pre) => pre.getAttribute('aria-label'))).toEqual([null, null]);
      expect(blocks.map((pre) => pre.getAttribute('role'))).toEqual([null, null]);
      expect(element.querySelector('.md-code__copy')?.getAttribute('aria-label')).toBe(
        'Copy ts code block',
      );
    });

    it('refuses a tabindex the model wrote, which is what admitting one costs', async () => {
      // `tabindex` rests on the parser layer too: raw HTML slipping past `renderer.html` would
      // let model text mint focusable elements anywhere.
      const element = await render(
        '<pre tabindex="0">x</pre>\n\n<div tabindex="0">y</div>\n\n<span tabindex="0">z</span>',
      );

      expect(element.querySelector('[tabindex]')).toBeNull();
      expect(element.querySelector('pre')).toBeNull();
      // `role` never entered the profile, so nothing can claim a semantic either.
      expect(sanitizeChatMarkdown('<pre role="region" tabindex="0">x</pre>')).not.toContain('role');
    });

    it('refuses an info string that is not a language, rather than escaping it', async () => {
      const element = await render('```ts"onload=alert(1)\nconst a = 1;\n```');

      const pre = element.querySelector('pre');
      // The character set stops at the quote, so nothing can break out of the attribute.
      expect(pre?.className).toBe('language-ts');
      expect(pre?.getAttribute('onload')).toBeNull();
      expect(alertSpy).not.toHaveBeenCalled();
    });
  });

  describe('the diagram branch', () => {
    it('marks a mermaid fence without changing anything else about it', async () => {
      const element = await render('```mermaid\ngraph TD; A-->B\n```');

      const wrapper = element.querySelector('.md-diagram');
      // Only a class on the ordinary chrome, so flag-off and parse-failure share one path.
      expect(wrapper?.classList.contains('md-code')).toBe(true);
      expect(wrapper?.querySelector('.md-code__lang')?.textContent).toBe('mermaid');
      expect(wrapper?.querySelector('.md-code__copy')).not.toBeNull();
    });

    it('escapes the diagram source, character for character', async () => {
      const source = 'graph TD\n  A["x < y & z"] --> B';
      const element = await render('```mermaid\n' + source + '\n```');

      // `ngx-markdown`'s own mermaid integration interpolates the source unescaped.
      expect(element.querySelector('.md-diagram pre > code')?.textContent).toBe(source + '\n');
      expect(element.querySelector('.md-diagram b')).toBeNull();
    });

    it('renders a hostile diagram body as inert text', async () => {
      const element = await render('```mermaid\n<img src=x onerror=alert(1)>\n```');

      // Shown as source text: displaying the payload, not running it.
      expect(element.querySelector('.md-diagram pre > code')?.textContent).toBe(
        '<img src=x onerror=alert(1)>\n',
      );
      expect(element.querySelector('img')).toBeNull();
      expect(alertSpy).not.toHaveBeenCalled();
    });

    it('refuses a hand-written diagram wrapper from model text', async () => {
      // The directive targets this class, so a minted one would pick the diagram source.
      const element = await render(
        '<div class="md-code md-diagram"><pre><code>x</code></pre></div>',
      );

      expect(element.querySelector('.md-diagram')).toBeNull();
    });
  });
});
