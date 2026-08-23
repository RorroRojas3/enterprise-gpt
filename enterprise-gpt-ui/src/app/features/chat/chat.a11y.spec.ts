import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TokenService } from '@core/auth/token-service';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { STREAM_FETCH } from '@core/stream/stream-fetch.token';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture, modelFixture } from '@testing/catalog';
import {
  conversationDetailFixture,
  conversationFixture,
  messageFixture,
} from '@testing/conversations';
import { CHAT_ROLE, MESSAGE_FEEDBACK_RATING } from '@domain/api/conversation';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Chat } from './chat';
import { chatMatcher } from './chat-route';

/** ISO 8601 with offset, as the transcript response carries it. */
const RATED_AT = '2026-08-21T09:30:00+00:00';

/**
 * The chat route in both themes — the empty screen of frame `1a` and an open
 * conversation with its header, composer and pickers.
 *
 * The composer is the densest control surface in the application: seven controls, two
 * anchored panels, a file input that is off-screen rather than hidden, and a send/stop
 * button that swaps identity mid-turn. It is also the surface US-1401 found two of its
 * defects on, which is why it is worth auditing with the catalogues actually loaded
 * rather than in its empty state.
 */

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

@Component({ template: '' })
class Blank {}

describe('chat accessibility (US-1405)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let attached: HTMLElement | null = null;

  beforeEach(async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        // Both flags off: this audit is about the screen's own semantics, and a
        // deployment with diagrams on would have it reach for a library over the network.
        provideTestAppConfig({ features: { diagrams: false, math: false, rawStreamCodec: false } }),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: vi.fn(() => new Promise<Response>(() => {})) },
        { provide: TokenService, useValue: { getToken: vi.fn().mockResolvedValue('token-1') } },
        provideRouter(
          [
            { matcher: chatMatcher, component: Chat },
            { path: '**', component: Blank },
          ],
          withComponentInputBinding(),
        ),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    attached?.remove();
    attached = null;
    clearTheme();
    // The transcript replay (US-410) is not what this spec is about; taking those reads
    // off the queue rather than answering them mirrors `chat.spec.ts`.
    backend.match((request) => request.url.endsWith('/messages'));
    backend.verify();
  });

  async function open(url: string, theme: 'light' | 'dark'): Promise<HTMLElement> {
    applyTheme(theme);
    await harness.navigateByUrl(url, Chat);

    const element = harness.routeNativeElement as HTMLElement;
    document.body.append(element);
    attached = element;
    return element;
  }

  /**
   * The composer's two pickers render nothing until their catalogues land.
   *
   * Both stores are root-scoped and load on demand, so the request only exists once
   * something asks — at runtime that is `SessionBootstrap`, and here it is this.
   */
  async function loadCatalogues(): Promise<void> {
    const models = TestBed.inject(ModelCatalogStore).ensureLoaded();
    const mcps = TestBed.inject(McpCatalogStore).ensureLoaded();

    backend.expectOne((request) => request.url === MODELS_URL).flush([modelFixture()]);
    backend
      .expectOne((request) => request.url === MCPS_URL)
      // One row with a brand mark and one without, so the audit covers both arms of the
      // picker's icon slot rather than only the generic glyph.
      .flush([
        mcpFixture({ name: 'Microsoft Learn', iconKey: 'microsoft' }),
        mcpFixture({ name: 'Jira Cloud', iconKey: null }),
      ]);
    await Promise.all([models, mcps]);
    await harness.fixture.whenStable();
  }

  for (const theme of THEMES) {
    it(`finds nothing serious on the empty chat screen in the ${theme} theme`, async () => {
      const element = await open('/chat', theme);
      await loadCatalogues();

      await expectNoSeriousViolations(element, `/chat empty (${theme})`);
    });

    it(`finds nothing serious on an open conversation in the ${theme} theme`, async () => {
      const conversation = conversationFixture({ name: 'Helios 2.4 release status' });
      const element = await open(`/chat/${conversation.id}`, theme);

      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}`)
        .flush(conversationDetailFixture({ ...conversation }));
      await loadCatalogues();

      await expectNoSeriousViolations(element, `/chat/:id (${theme})`);
    });

    /**
     * The download menu open (US-1502, frame `2f`).
     *
     * Audited open rather than closed because the panel is where its semantics live: a
     * `role="menu"` whose children must all be `menuitem`, one of which the note beside
     * them is deliberately not. The trigger is `<app-menu>`'s, which carries a tooltip
     * only while disabled — the case that would otherwise put an `aria-label` on a
     * role-less element, which axe reports as serious.
     */
    it(`finds nothing serious with the download menu open in the ${theme} theme`, async () => {
      const conversation = conversationFixture({ name: 'Helios 2.4 release status' });
      const element = await open(`/chat/${conversation.id}`, theme);

      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}`)
        .flush(conversationDetailFixture({ ...conversation }));
      // A transcript, because the control is absent without one.
      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}/messages`)
        .flush({
          id: conversation.id,
          name: conversation.name,
          messages: [{ text: 'What shipped?', role: 3 }],
        });
      await loadCatalogues();

      element
        .querySelector<HTMLButtonElement>(
          '.chat__header app-conversation-download-menu .menu__trigger',
        )
        ?.click();
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/chat/:id download menu (${theme})`);
    });

    /**
     * The Tools picker open (US-403/US-417/US-418, frame `2c`).
     *
     * Audited open for the reason the download menu is, and one more: its rows are
     * `menuitemcheckbox` carrying an `aria-hidden` switch that is not a `role="switch"`,
     * because that role is not an allowed owned element of `role="menu"`. That is exactly
     * what `aria-required-children` and `aria-hidden-focus` are for, and the closed pill
     * exercises neither.
     */
    it(`finds nothing serious with the tools picker open in the ${theme} theme`, async () => {
      const element = await open('/chat', theme);
      await loadCatalogues();

      element.querySelector<HTMLButtonElement>('app-tools-menu .menu__trigger')?.click();
      await harness.fixture.whenStable();

      expect(element.querySelector('.tools-menu__option')).not.toBeNull();
      await expectNoSeriousViolations(element, `/chat tools picker (${theme})`);
    });

    /**
     * US-1103's thumbs, which no other audit reaches: the transcript above flushes a
     * message with no `id`, so the controls are correctly absent there and would stay
     * silently unaudited for ever.
     *
     * The answer is flushed **already rated**, and that is what makes the audit run at
     * all. The footer is `opacity: 0` at rest and axe skips a zero-opacity subtree for
     * every visibility-dependent rule — colour contrast and target size among them, which
     * are the two an icon-only toggle pair is most likely to fail. A rated turn is the one
     * state that forces the row visible without a synthetic hover.
     */
    it(`finds nothing serious with a rated answer in the ${theme} theme`, async () => {
      const conversation = conversationFixture({ name: 'Helios 2.4 release status' });
      const element = await open(`/chat/${conversation.id}`, theme);

      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}`)
        .flush(conversationDetailFixture({ ...conversation }));
      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}/messages`)
        .flush({
          id: conversation.id,
          name: conversation.name,
          messages: [
            messageFixture({ text: 'What shipped?', role: CHAT_ROLE.user }),
            messageFixture({
              text: 'The RC build.',
              role: CHAT_ROLE.assistant,
              feedback: { rating: MESSAGE_FEEDBACK_RATING.up, dateModified: RATED_AT },
            }),
          ],
        });
      await loadCatalogues();
      await harness.fixture.whenStable();

      // Not merely present: *revealed*. Axe skips a zero-opacity subtree for every
      // visibility-dependent rule — target size and colour contrast among them, which are
      // the two this audit exists to measure — so a regression in the `--rated` reveal
      // would quietly turn this into a green no-op rather than a failure.
      const footer = element.querySelector('.assistant-turn__footer');
      expect(footer?.querySelector('app-message-feedback')).not.toBeNull();
      expect(getComputedStyle(footer as Element).opacity).toBe('1');

      await expectNoSeriousViolations(element, `/chat/:id rated answer (${theme})`);
    });
  }
});
