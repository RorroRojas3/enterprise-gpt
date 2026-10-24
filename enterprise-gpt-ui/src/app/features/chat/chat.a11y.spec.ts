import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { getAllByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CHAT_ROLE, MESSAGE_FEEDBACK_RATING } from '@core/api/conversation.model';
import { AccessTokenClient } from '@core/auth/access-token-client';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { chatMatcher } from '@core/navigation/chat-matcher';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture, modelFixture } from '@testing/catalog';
import {
  conversationDetailFixture,
  conversationFixture,
  messageFixture,
} from '@testing/conversations';
import { openMenu } from '@testing/overlays';
import { Chat } from './chat';
import { STREAM_FETCH } from './stream-fetch-token';

/** ISO 8601 with offset, as the transcript response carries it. */
const RATED_AT = '2026-08-21T09:30:00+00:00';

/**
 * Audited with the catalogues loaded: the composer is the densest control surface in the app,
 * and most of it is absent without them.
 */

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

@Component({ template: '' })
class Blank {}

describe('chat accessibility', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let attached: HTMLElement | null = null;

  beforeEach(async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        // Diagrams on would fetch a library over the network mid-audit.
        provideTestAppConfig({ features: { diagrams: false, math: false, rawStreamCodec: false } }),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: vi.fn(() => new Promise<Response>(() => {})) },
        {
          provide: AccessTokenClient,
          useValue: { getToken: vi.fn().mockResolvedValue('token-1') },
        },
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
    // Transcript replay reads are not under test here; drain them unanswered.
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

  /** The pickers render nothing without catalogues, which only `SessionBootstrap` requests. */
  async function loadCatalogues(): Promise<void> {
    const models = TestBed.inject(ModelCatalogStore).ensureLoaded();
    const mcps = TestBed.inject(McpCatalogStore).ensureLoaded();

    backend.expectOne((request) => request.url === MODELS_URL).flush([modelFixture()]);
    backend
      .expectOne((request) => request.url === MCPS_URL)
      // With and without a brand mark: both arms of the picker's icon slot.
      .flush([
        mcpFixture({ name: 'Microsoft Learn', iconKey: 'microsoft' }),
        mcpFixture({ name: 'Jira Cloud', iconKey: null }),
        // Both per-user key states; the configured one puts two menu items on one line.
        mcpFixture({ name: 'GitHub', iconKey: 'github', requiresUserApiKey: true }),
        mcpFixture({
          name: 'GitLab',
          iconKey: null,
          requiresUserApiKey: true,
          hasUserApiKey: true,
          apiKeyHint: 'wxyz',
        }),
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

    /** Audited open: the menu holds a note that is deliberately not a `menuitem`. */
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

      // The header's: the composer renders the same menu.
      await openMenu(harness.fixture, /^Download /, element.querySelector('header')!);

      await expectNoSeriousViolations(element, `/chat/:id download menu (${theme})`);
    });

    /**
     * Audited open: each `menuitemcheckbox` row carries an `aria-hidden` switch, which the closed
     * pill never exercises.
     */
    it(`finds nothing serious with the tools picker open in the ${theme} theme`, async () => {
      const element = await open('/chat', theme);
      await loadCatalogues();

      const menu = await openMenu(harness.fixture, /^Tool servers:/, element);

      expect(getAllByRole(menu, 'menuitemcheckbox').length).toBeGreaterThan(0);
      await expectNoSeriousViolations(element, `/chat tools picker (${theme})`);
    });

    /**
     * Flushed already rated: the footer is `opacity: 0` at rest, and axe skips zero-opacity
     * subtrees for contrast and target size. A rated turn shows it without a synthetic hover.
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

      // Revealed, not merely present: a hidden footer would turn this audit into a silent pass.
      const footer = element.querySelector('.assistant-turn__footer');
      expect(footer?.querySelector('app-message-feedback')).not.toBeNull();
      expect(getComputedStyle(footer as Element).opacity).toBe('1');

      await expectNoSeriousViolations(element, `/chat/:id rated answer (${theme})`);
    });

    /** The only transcript here with attachments, so the only audit of the generated-file chips. */
    it(`finds nothing serious on a generated file in the ${theme} theme`, async () => {
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
            messageFixture({ text: 'Make me a spreadsheet.', role: CHAT_ROLE.user }),
            messageFixture({
              text: 'Here is the workbook.',
              role: CHAT_ROLE.assistant,
              // Two, so the audit covers the wrapped row and a second glyph tone.
              attachments: [
                {
                  id: '9c8b7a65-4321-4fed-8cba-0987654321fe',
                  name: 'regional-summary.xlsx',
                  extension: '.xlsx',
                  mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
                  size: 2048,
                },
                {
                  id: '1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d',
                  name: 'meeting-summary.pptx',
                  extension: '.pptx',
                  mimeType:
                    'application/vnd.openxmlformats-officedocument.presentationml.presentation',
                  size: 51_200,
                },
              ],
            }),
          ],
        });
      await loadCatalogues();
      await harness.fixture.whenStable();

      // Visible as well as rendered: axe cannot fail a chip it cannot see.
      const chips = getAllByRole(element, 'group', { name: /^Generated file, / });

      expect(chips).toHaveLength(2);
      expect(getAllByRole(element, 'button', { name: /^Download generated file / })).toHaveLength(
        2,
      );
      expect(getComputedStyle(chips[0] as Element).opacity).toBe('1');

      await expectNoSeriousViolations(element, `/chat/:id generated file (${theme})`);
    });
  }
});
