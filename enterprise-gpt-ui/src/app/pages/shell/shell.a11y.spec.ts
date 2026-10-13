import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { ApplicationRef, ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { getByRole, queryByRole } from '@testing-library/dom';
import { userEvent } from '@vitest/browser/context';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
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
import { tooltipOf } from '@testing/overlays';
import { projectFixture, projectPage } from '@testing/projects';
import { administratorFixture } from '@testing/session';
import { Shell } from './shell';

/**
 * The one audit of the whole chrome, so the only one leaving axe's `region` rule on: the others
 * mount screens in a bare host, where it would fire on the harness. Also gates horizontal
 * overflow, which jsdom cannot measure.
 */

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;
const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

/** The one starred project every render in this file puts in the sidebar. */
const FAVORITE = projectFixture({ name: 'Data Platform Migration', isFavorite: true });

@Component({
  selector: 'app-blank',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class Blank {}

describe('application frame accessibility', () => {
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

  /**
   * `openSidebar` opens the tablet overlay or the mobile drawer first, so the expanded panel is
   * audited at every width rather than only where it sits in the flow.
   */
  async function renderShell(
    theme: 'light' | 'dark',
    { openSidebar = false } = {},
  ): Promise<HTMLElement> {
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

    // With a node expanded, or the audit would miss its panel, nested links and `aria-controls`.
    TestBed.inject(ProjectLookupStore).ensureLoaded();
    TestBed.tick();
    backend
      .expectOne((request) => request.url === PROJECTS_URL)
      .flush(projectPage([FAVORITE, projectFixture({ name: 'Hiring' })]));
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    if (openSidebar) {
      getByRole(element, 'button', { name: /^(Expand sidebar|Open navigation)$/ }).click();
      await fixture.whenStable();
    }

    // Only the expanded panel has the tree; the tablet strip and the shut drawer do not.
    const disclosure = queryByRole(element, 'button', {
      name: `Conversations in ${FAVORITE.name}`,
    });
    if (disclosure !== null) {
      disclosure.click();
      await fixture.whenStable();

      backend
        .expectOne(
          (request) =>
            request.url === SEARCH_URL && request.params.get('projectId') === FAVORITE.id,
        )
        .flush(conversationPage([conversationFixture({ name: 'Warehouse sync triage' })]));
      await fixture.whenStable();
    }

    document.body.append(element);
    attached = element;
    // Last: the footer's theme toggle constructs `ThemeService`, which writes the stored or
    // system theme over anything set before it renders.
    applyTheme(theme);
    return element;
  }

  for (const width of Object.keys(VIEWPORTS) as ViewportName[]) {
    for (const theme of THEMES) {
      it(`finds nothing serious at the ${width} width in the ${theme} theme`, async () => {
        await atViewport(width);
        const element = await renderShell(theme);

        // `region` left on: this frame owns the landmarks, so the rule has something real to judge.
        await expectNoSeriousViolations(element, `shell ${width} (${theme})`, { rules: {} });
      });

      it(`keeps its content inside the viewport at the ${width} width in ${theme}`, async () => {
        await atViewport(width);
        const element = await renderShell(theme);

        expectNoHorizontalOverflow(element, `shell ${width} (${theme})`);
      });
    }
  }

  for (const width of ['tablet', 'mobile'] as const) {
    for (const theme of THEMES) {
      it(`finds nothing serious with the sidebar opened at the ${width} width in ${theme}`, async () => {
        await atViewport(width);
        const element = await renderShell(theme, { openSidebar: true });

        await expectNoSeriousViolations(element, `shell ${width} open (${theme})`, { rules: {} });
        expectNoHorizontalOverflow(element, `shell ${width} open (${theme})`);
      });
    }
  }

  it('names the favourites disclosure and points it at a panel that exists', async () => {
    const element = await renderShell('light');

    // A name that doesn't repeat `aria-expanded`'s state, and a reference axe can resolve.
    const disclosure = getByRole(element, 'button', { name: `Conversations in ${FAVORITE.name}` });
    expect(disclosure.getAttribute('aria-expanded')).toBe('true');
    const panelId = disclosure.getAttribute('aria-controls');
    expect(panelId).toBe(`sidebar-project-${FAVORITE.id}`);
    expect(element.querySelector(`#${panelId}`)).not.toBeNull();
  });

  /**
   * Toggling moves focus to a replacement chevron the pointer never entered, so it never sees
   * `mouseleave`. Trusted input throughout: the engine reads focus modality from it alone.
   */
  describe('the tooltip on the chevron focus follows to', () => {
    async function collapseSidebar(element: HTMLElement): Promise<HTMLButtonElement> {
      await TestBed.inject(ApplicationRef).whenStable();

      return getByRole<HTMLButtonElement>(element, 'button', { name: 'Expand sidebar' });
    }

    it('stays away after a pointer collapse', async () => {
      await atViewport('desktop');
      const element = await renderShell('light');
      const collapse = getByRole(element, 'button', { name: 'Collapse sidebar' });

      await userEvent.click(collapse);
      const expand = await collapseSidebar(element);

      expect(document.activeElement).toBe(expand);
      expect(tooltipOf(expand)).toBeNull();
    });

    it('names the replacement after a keyboard collapse', async () => {
      await atViewport('desktop');
      const element = await renderShell('light');
      const collapse = getByRole(element, 'button', { name: 'Collapse sidebar' });

      // Tab for the modality, then focus the chevron directly; scripted focus carries the
      // keyboard modality forward.
      await userEvent.keyboard('{Tab}');
      collapse.focus();
      await userEvent.keyboard('{Enter}');
      const expand = await collapseSidebar(element);

      expect(document.activeElement).toBe(expand);
      expect(tooltipOf(expand)?.textContent?.trim()).toBe('Expand sidebar');
    });
  });

  it('renders one navigation landmark, whatever the width', async () => {
    // Re-checks `shell-responsive.spec.ts`'s invariant with real media queries.
    await atViewport('mobile');
    const element = await renderShell('light');

    expect(element.querySelectorAll('#sidebar-primary').length).toBeLessThanOrEqual(1);
    expect(element.querySelector('app-mobile-navbar')).not.toBeNull();
  });
});
