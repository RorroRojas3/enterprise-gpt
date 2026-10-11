import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ConversationExportStore } from '@core/conversations/conversation-export-store';
import { ConversationDownloadMenu } from './conversation-download-menu';

const CONVERSATION_ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';
const EXPORT_URL = `${TEST_API_BASE_URL}/api/conversations/${CONVERSATION_ID}/export`;

@Component({
  selector: 'app-download-menu-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ConversationDownloadMenu],
  template: `
    <app-conversation-download-menu
      [conversationId]="conversationId()"
      conversationName="Helios release"
      [disabled]="disabled()"
    />
  `,
})
class MenuHost {
  readonly disabled = signal(false);
  readonly conversationId = signal(CONVERSATION_ID);
}

describe('ConversationDownloadMenu', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });
    backend = TestBed.inject(HttpTestingController);

    vi.stubGlobal('URL', {
      ...URL,
      createObjectURL: vi.fn(() => 'blob:test/0'),
      revokeObjectURL: vi.fn(),
    });
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
  });

  afterEach(() => {
    backend.verify();
    // The store's ten-second revoke outlives the test, and fake timers would stall
    // `whenStable()`. Tearing the stub down first leaves the late timer nothing to write
    // into; the delay is asserted in `conversation-export-store.spec.ts`.
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  async function render() {
    const fixture = TestBed.createComponent(MenuHost);
    // Attached to the document because the panel places itself from the trigger's box in
    // an after-render effect, and a detached element measures as zero.
    document.body.append(fixture.nativeElement as HTMLElement);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;

    return {
      fixture,
      host,
      trigger: () => host.querySelector('.menu__trigger') as HTMLButtonElement,
      panel: () => host.querySelector('[role="menu"]'),
      items: () => [...host.querySelectorAll<HTMLButtonElement>('[appMenuItem]')],
      async open() {
        (host.querySelector('.menu__trigger') as HTMLButtonElement).click();
        await fixture.whenStable();
      },
      async choose(label: string) {
        const item = [...host.querySelectorAll<HTMLButtonElement>('[appMenuItem]')].find((button) =>
          button.textContent?.includes(label),
        );
        item?.click();
        await fixture.whenStable();
      },
    };
  }

  /** One item per offered format, in this order, each with its own glyph. */
  it('offers Markdown, Word, PDF and HTML with their glyphs', async () => {
    const menu = await render();
    await menu.open();

    const items = menu.items();
    expect(items.map((item) => item.textContent?.trim())).toEqual([
      'Markdown (.md)',
      'Word (.docx)',
      'PDF',
      'HTML',
    ]);
    expect(menu.host.querySelector('.download-menu__glyph--md')).not.toBeNull();
    expect(menu.host.querySelector('.download-menu__glyph--docx')).not.toBeNull();
    expect(menu.host.querySelector('.download-menu__glyph--pdf')).not.toBeNull();
    expect(menu.host.querySelector('.download-menu__glyph--html')).not.toBeNull();
  });

  /** The row-to-token wiring, which is the whole of what choosing a format does. */
  it.each([
    ['Markdown (.md)', 'md'],
    ['Word (.docx)', 'docx'],
    ['PDF', 'pdf'],
    ['HTML', 'html'],
  ])('requests %s as format=%s', async (label, format) => {
    const menu = await render();
    await menu.open();
    await menu.choose(label);

    const request = backend.expectOne((candidate) => candidate.url === EXPORT_URL);

    expect(request.request.params.get('format')).toBe(format);
    request.flush(new Blob(['x']));
  });

  /** The note is a note, not another stop in the panel's roving focus. */
  it('carries the unsaved-answer note outside the item list', async () => {
    const menu = await render();
    await menu.open();

    const note = menu.host.querySelector('.download-menu__note');
    expect(note?.textContent).toContain('stopped, unsaved answer');
    expect(note?.hasAttribute('appMenuItem')).toBe(false);
    expect(menu.items()).toHaveLength(4);
  });

  /** The chosen item names itself, the rest dim, and the panel stays up. */
  it('shows the chosen format preparing and dims the rest, without closing', async () => {
    const menu = await render();
    await menu.open();
    await menu.choose('PDF');

    const request = backend.expectOne((candidate) => candidate.url === EXPORT_URL);

    expect(menu.panel()).not.toBeNull();
    const items = menu.items();
    expect(items[2]?.textContent).toContain('Preparing PDF…');
    expect(items[2]?.querySelector('.download-menu__spinner')).not.toBeNull();
    expect(items[0]?.classList.contains('download-menu__item--dimmed')).toBe(true);
    expect(items[1]?.classList.contains('download-menu__item--dimmed')).toBe(true);
    expect(items[2]?.classList.contains('download-menu__item--dimmed')).toBe(false);

    request.flush(new Blob(['x']));
  });

  it('closes once the download has been handed to the browser', async () => {
    const menu = await render();
    await menu.open();
    await menu.choose('Markdown');

    backend.expectOne((candidate) => candidate.url === EXPORT_URL).flush(new Blob(['x']));
    await menu.fixture.whenStable();

    expect(menu.panel()).toBeNull();
    // Closing through the two-way model bypasses `Menu.close()`, so without the rescue focus
    // would land on <body> when the focused item is destroyed.
    expect(document.activeElement).toBe(menu.trigger());
  });

  /** The store runs one export at a time and silently drops the rest, so this menu dims too. */
  it('dims every item while another conversation is exporting', async () => {
    const menu = await render();
    await menu.open();
    await menu.choose('PDF');
    const request = backend.expectOne((candidate) => candidate.url === EXPORT_URL);

    menu.fixture.componentInstance.conversationId.set('11111111-2222-4333-8444-555555555555');
    await menu.fixture.whenStable();

    const items = menu.items();
    expect(items.every((item) => item.classList.contains('download-menu__item--dimmed'))).toBe(
      true,
    );
    expect(items.every((item) => item.getAttribute('aria-disabled') === 'true')).toBe(true);
    expect(items.some((item) => item.textContent?.includes('Preparing'))).toBe(false);

    request.flush(new Blob(['x']));
  });

  /** The busy row is undimmed and just chosen, so busy, not unavailable, is true of it. */
  it('marks the working item busy and the rest disabled', async () => {
    const menu = await render();
    await menu.open();
    await menu.choose('PDF');
    const request = backend.expectOne((candidate) => candidate.url === EXPORT_URL);

    const items = menu.items();
    expect(items[2]?.getAttribute('aria-busy')).toBe('true');
    expect(items[2]?.getAttribute('aria-disabled')).toBeNull();
    expect(items[0]?.getAttribute('aria-disabled')).toBe('true');
    expect(items[1]?.getAttribute('aria-disabled')).toBe('true');

    request.flush(new Blob(['x']));
  });

  /** Menu mode visits only `menuitem`s, so the caveat has to reach them as a description. */
  it('describes every item with the unsaved-answer note', async () => {
    const menu = await render();
    await menu.open();

    const noteId = menu.host.querySelector('.download-menu__note')?.id;
    expect(noteId).toBeTruthy();
    expect(menu.items().every((item) => item.getAttribute('aria-describedby') === noteId)).toBe(
      true,
    );
  });

  /** On a failure the panel stays up, so another format is one click away. */
  it('returns to idle but stays open when the export fails', async () => {
    const menu = await render();
    await menu.open();
    await menu.choose('PDF');

    backend
      .expectOne((candidate) => candidate.url === EXPORT_URL)
      .flush(new Blob([JSON.stringify(PROBLEM_FIXTURES.exportRendererNotConfigured)]), {
        status: 503,
        statusText: 'Service Unavailable',
      });
    await menu.fixture.whenStable();

    expect(menu.panel()).not.toBeNull();
    expect(menu.items()[2]?.textContent?.trim()).toBe('PDF');
    expect(menu.items()[0]?.classList.contains('download-menu__item--dimmed')).toBe(false);
  });

  /** A second press on another format while one prepares is a mis-click, not a request. */
  it('ignores a second choice while one is preparing', async () => {
    const menu = await render();
    await menu.open();
    await menu.choose('PDF');
    await menu.choose('Markdown');

    const requests = backend.match((candidate) => candidate.url === EXPORT_URL);
    expect(requests).toHaveLength(1);
    requests[0]?.flush(new Blob(['x']));
  });

  /** The trigger owes a reason when it is unavailable, not just a dimmed look. */
  it('disables the trigger with a reason while a turn streams', async () => {
    const menu = await render();
    menu.fixture.componentInstance.disabled.set(true);
    await menu.fixture.whenStable();

    expect(menu.trigger().getAttribute('aria-disabled')).toBe('true');
    // The tooltip is visual only, so the reason is also in the name, which with no projected
    // trigger face is Menu's own visually-hidden span.
    expect(menu.trigger().textContent).toContain('Available when the response finishes');
  });

  it('does not open, or request, while disabled', async () => {
    const menu = await render();
    menu.fixture.componentInstance.disabled.set(true);
    await menu.fixture.whenStable();

    await menu.open();

    expect(menu.panel()).toBeNull();
    backend.expectNone((candidate) => candidate.url === EXPORT_URL);
  });

  it('names the conversation in the trigger, so the control says what it acts on', async () => {
    const menu = await render();

    expect(menu.trigger().textContent?.trim()).toBe('Download Helios release');
  });

  /**
   * Both menus can be mounted and the store's outcome is replaced, not consumed, so another
   * conversation's outcome must not close this one.
   */
  it('ignores an outcome for a different conversation', async () => {
    const menu = await render();
    await menu.open();

    TestBed.inject(ConversationExportStore).download('11111111-2222-4333-8444-555555555555', 'md');
    backend
      .expectOne(
        (candidate) =>
          candidate.url ===
          `${TEST_API_BASE_URL}/api/conversations/11111111-2222-4333-8444-555555555555/export`,
      )
      .flush(new Blob(['x']));
    await menu.fixture.whenStable();

    expect(menu.panel()).not.toBeNull();
  });
});
