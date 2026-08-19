import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { projectFixture, projectPage } from '@testing/projects';
import { afterEach, beforeEach, describe, it } from 'vitest';
import { Conversations } from '../conversations/conversations';
import { Projects } from './projects';

/**
 * The two library screens, in both themes.
 *
 * They share a shape — a title, a wrapping toolbar, then a collection — and between them
 * they exercise most of the shared kit: `DataTable` and its selection checkboxes, the
 * paginator, the bulk action bar, `ProjectCard` and the row kebabs. Both are rendered
 * with rows rather than empty, because an empty collection has none of that in it.
 */

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;
const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

describe('library accessibility (US-1405)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let attached: HTMLElement | null = null;

  beforeEach(async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(
          [
            { path: 'projects', component: Projects },
            { path: 'conversations', component: Conversations },
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
    backend.verify();
  });

  async function open(url: string, theme: 'light' | 'dark'): Promise<HTMLElement> {
    applyTheme(theme);
    await harness.navigateByUrl(url);

    const element = harness.routeNativeElement as HTMLElement;
    document.body.append(element);
    attached = element;
    return element;
  }

  for (const theme of THEMES) {
    it(`finds nothing serious on the projects grid in the ${theme} theme`, async () => {
      const element = await open('/projects', theme);

      backend
        .expectOne((request) => request.url === PROJECTS_URL)
        .flush(
          projectPage([
            projectFixture({ name: 'Data Platform Migration' }),
            projectFixture({ name: 'Helios' }),
          ]),
        );
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/projects (${theme})`);
    });

    it(`finds nothing serious on the conversation library in the ${theme} theme`, async () => {
      const element = await open('/conversations', theme);

      backend
        .expectOne((request) => request.url === SEARCH_URL)
        .flush(
          conversationPage([
            conversationFixture({ name: 'Helios 2.4 release status' }),
            conversationFixture({ name: 'Migration plan' }),
          ]),
        );
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/conversations (${theme})`);
    });
  }
});
