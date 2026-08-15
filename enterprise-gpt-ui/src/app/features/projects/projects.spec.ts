import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { projectFixture, projectPage } from '@testing/projects';
import { Projects } from './projects';

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

/**
 * `SearchInput`'s own 300 ms window, with room for the effect that follows it and for a
 * loaded runner — real timers, because faking the clock also stalls the scheduler
 * `whenStable()` awaits.
 */
const DEBOUNCE_MS = 450;

describe('Projects (US-901)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'projects', component: Projects }], withComponentInputBinding()),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    // The harness navigates directly and never starts the router's popstate listener,
    // so without this a back press moves `Location` and leaves the router — and the
    // screen — on the URL it already had.
    TestBed.inject(Router).setUpLocationChangeListener();
  });

  afterEach(() => {
    backend.verify();
  });

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function expectRequest(): TestRequest {
    return backend.expectOne((request) => request.url === PROJECTS_URL);
  }

  async function open(url = '/projects'): Promise<void> {
    await harness.navigateByUrl(url);
  }

  async function type(text: string): Promise<void> {
    const field = element().querySelector<HTMLInputElement>('.search__field');
    field!.value = text;
    field!.dispatchEvent(new Event('input'));
    await new Promise((resolve) => setTimeout(resolve, DEBOUNCE_MS));
    await harness.fixture.whenStable();
  }

  it('issues exactly one request for a deep link that already carries a term', async () => {
    await open('/projects?name=helios');

    // The whole reason `bindQuery` takes a computation: binding values would fire an
    // unfiltered request first and a filtered one second.
    const request = expectRequest();
    expect(request.request.params.get('name')).toBe('helios');
    request.flush(projectPage([projectFixture()]));
    await harness.fixture.whenStable();

    backend.verify();
  });

  it('writes the term into the URL, which is the only thing that drives the query', async () => {
    await open();
    expectRequest().flush(projectPage([projectFixture()]));
    await harness.fixture.whenStable();

    await type('helios');

    expect(TestBed.inject(Router).url).toBe('/projects?name=helios');
    expectRequest().flush(projectPage([]));
    await harness.fixture.whenStable();
  });

  it('restores the unfiltered grid on back, because turning a filter on pushes', async () => {
    await open();
    expectRequest().flush(projectPage([projectFixture()]));
    await harness.fixture.whenStable();

    await type('helios');
    expectRequest().flush(projectPage([]));
    await harness.fixture.whenStable();

    TestBed.inject(Location).back();
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/projects');
    expectRequest().flush(projectPage([projectFixture()]));
    await harness.fixture.whenStable();
  });

  it('names the term in the no-matches state and offers a way out', async () => {
    await open('/projects?name=helios');
    expectRequest().flush(projectPage([]));
    await harness.fixture.whenStable();

    const empty = element().querySelector('app-empty-state');
    expect(empty?.textContent).toContain('No projects match “helios”');
  });

  it('offers a create action to a reader with no projects at all', async () => {
    await open();
    expectRequest().flush(projectPage([]));
    await harness.fixture.whenStable();

    expect(element().querySelector('app-empty-state')?.textContent).toContain('No projects yet');

    element().querySelector<HTMLButtonElement>('app-empty-state button')?.click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(ProjectActionsStore).formMode()).toBe('create');
  });

  it('replaces the grid with an error panel rather than showing a half-drained set', async () => {
    await open();
    expectRequest().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Server Error',
    });
    await harness.fixture.whenStable();

    expect(element().querySelector('app-error-panel')).not.toBeNull();
    expect(element().querySelector('app-project-card')).toBeNull();
  });

  it('renders a card per project, with no favourite star until US-909', async () => {
    await open();
    expectRequest().flush(projectPage([projectFixture({ name: 'Helios' }), projectFixture()]));
    await harness.fixture.whenStable();

    const cards = element().querySelectorAll('app-project-card');
    expect(cards.length).toBe(2);
    // Absent, not disabled: nothing on the wire can persist a press.
    expect(element().querySelector('.project-card__favorite')).toBeNull();
    // The kebab is there, because both of its items work today.
    expect(element().querySelector('app-project-card app-menu')).not.toBeNull();
  });

  it('opens the create dialog from the toolbar', async () => {
    await open();
    expectRequest().flush(projectPage([projectFixture()]));
    await harness.fixture.whenStable();

    element().querySelector<HTMLButtonElement>('.projects__new')?.click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(ProjectActionsStore).formMode()).toBe('create');
  });

  it('states the ceiling rather than letting the grid quietly stop', async () => {
    await open();

    for (let page = 0; page < 5; page++) {
      expectRequest().flush(
        projectPage(
          Array.from({ length: 100 }, () => projectFixture()),
          { totalCount: 1284, skip: page * 100 },
        ),
      );
      await harness.fixture.whenStable();
    }

    expect(element().querySelector('.projects__ceiling')?.textContent).toContain(
      'Showing the 500 most recent of 1284 projects.',
    );
  });
});
