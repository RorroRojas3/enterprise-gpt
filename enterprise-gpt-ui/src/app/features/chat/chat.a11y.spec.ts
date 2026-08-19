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
import { conversationDetailFixture, conversationFixture } from '@testing/conversations';
import { afterEach, beforeEach, describe, it, vi } from 'vitest';
import { Chat } from './chat';
import { chatMatcher } from './chat-route';

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
    backend.expectOne((request) => request.url === MCPS_URL).flush([mcpFixture()]);
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
  }
});
