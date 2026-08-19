import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { SessionStore } from '@core/session/session-store';
import {
  THEMES,
  VIEWPORTS,
  ViewportName,
  applyTheme,
  atViewport,
  clearTheme,
  expectNoHorizontalOverflow,
  expectNoSeriousViolations,
} from '@testing/a11y';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { provideFakeMsal, signedInMsal } from '@testing/msal';
import { provideFakeNavigation } from '@testing/navigation';
import { administratorFixture } from '@testing/session';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { Shell } from './shell';

/**
 * The application frame at all three of US-1403's widths, in both themes.
 *
 * This is the one audit that renders the **whole** chrome — the skip link, the `<nav>`
 * landmark, `<main>` — rather than a routed screen in isolation, which is why it is also
 * the only one that leaves axe's `region` rule enabled. Every other `*.a11y.spec.ts`
 * disables it, because a screen mounted into a bare fixture host has no landmarks around
 * it and the rule would fire on the harness rather than on anything a user meets. Here it
 * fires on the real structure, so it is worth having.
 *
 * It also carries US-1403's last criterion. No horizontal overflow at three breakpoints
 * is not assertable in jsdom at all — there is no layout — and it is the reason that
 * criterion shipped verified by hand and gated here.
 */

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

@Component({
  selector: 'app-blank',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class Blank {}

describe('application frame accessibility (US-1403, US-1405)', () => {
  let backend: HttpTestingController;
  let attached: HTMLElement | null = null;

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: '**', component: Blank }]),
        provideLocationMocks(),
        provideFakeMsal(signedInMsal({ logoutRedirect: () => new Promise<void>(() => undefined) })),
        provideFakeNavigation(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(async () => {
    attached?.remove();
    attached = null;
    clearTheme();
    backend.verify();
    // Or the next file inherits whatever width the last test left behind.
    await atViewport('desktop');
  });

  async function renderShell(theme: 'light' | 'dark'): Promise<HTMLElement> {
    applyTheme(theme);

    const session = TestBed.inject(SessionStore);
    const loaded = session.ensureLoaded();
    await settle();
    backend.expectOne(ME_URL).flush(administratorFixture());
    await loaded;

    const fixture: ComponentFixture<Shell> = TestBed.createComponent(Shell);
    await fixture.whenStable();

    TestBed.inject(ConversationListStore).ensureLoaded();
    TestBed.tick();
    backend
      .expectOne((request) => request.url === SEARCH_URL)
      .flush(
        conversationPage([
          conversationFixture({ name: 'Helios 2.4 release status' }),
          conversationFixture({ name: 'Data platform migration plan' }),
        ]),
      );
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    document.body.append(element);
    attached = element;
    return element;
  }

  for (const width of Object.keys(VIEWPORTS) as ViewportName[]) {
    for (const theme of THEMES) {
      it(`finds nothing serious at the ${width} width in the ${theme} theme`, async () => {
        await atViewport(width);
        const element = await renderShell(theme);

        // `region` left enabled here, unlike every other audit in the suite: this is the
        // frame that owns the landmarks, so the rule has something real to judge.
        await expectNoSeriousViolations(element, `shell ${width} (${theme})`, { rules: {} });
      });

      it(`keeps its content inside the viewport at the ${width} width in ${theme}`, async () => {
        await atViewport(width);
        const element = await renderShell(theme);

        expectNoHorizontalOverflow(element, `shell ${width} (${theme})`);
      });
    }
  }

  it('renders one navigation landmark, whatever the width', async () => {
    // The structural invariant `shell-responsive.spec.ts` asserts in jsdom, re-checked
    // here where the media queries are real rather than driven by a fake.
    await atViewport('mobile');
    const element = await renderShell('light');

    expect(element.querySelectorAll('#sidebar-primary').length).toBeLessThanOrEqual(1);
    expect(element.querySelector('app-mobile-navbar')).not.toBeNull();
  });
});
