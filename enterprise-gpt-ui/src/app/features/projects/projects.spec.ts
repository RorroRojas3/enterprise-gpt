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

  it('renders a card per project, each with a favourite star and a kebab', async () => {
    await open();
    expectRequest().flush(projectPage([projectFixture({ name: 'Helios' }), projectFixture()]));
    await harness.fixture.whenStable();

    const cards = element().querySelectorAll('app-project-card');
    expect(cards.length).toBe(2);
    // Live since US-909 put `isFavorite` on the listing shape; it was withheld before
    // that rather than rendered inert.
    expect(element().querySelectorAll('.project-card__favorite').length).toBe(2);
    expect(element().querySelector('app-project-card app-menu')).not.toBeNull();
  });

  it('stars a project through its own route and reflects the server-confirmed flag', async () => {
    await open();
    const project = projectFixture({ name: 'Helios' });
    expectRequest().flush(projectPage([project]));
    await harness.fixture.whenStable();

    const star = element().querySelector<HTMLButtonElement>('.project-card__favorite');
    expect(star?.getAttribute('aria-label')).toBe('Favourite Helios');
    star?.click();
    await harness.fixture.whenStable();

    const request = backend.expectOne(`${PROJECTS_URL}/${project.id}/favorite`);
    expect(request.request.method).toBe('PUT');
    // A set, not a toggle: the body carries the state being asked for.
    expect(request.request.body).toEqual({ isFavorite: true });

    // Nothing moves until the 204 — every project write here is confirm-then-render.
    expect(element().querySelector('.project-card__favorite')?.getAttribute('aria-label')).toBe(
      'Favourite Helios',
    );

    request.flush(null, { status: 204, statusText: 'No Content' });
    await harness.fixture.whenStable();

    expect(element().querySelector('.project-card__favorite')?.getAttribute('aria-label')).toBe(
      'Unfavourite Helios',
    );
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
      'Showing the first 500 of 1284 projects, most recently updated first.',
    );
  });

  describe('the sort control (US-902)', () => {
    function select(): HTMLSelectElement {
      const control = element().querySelector<HTMLSelectElement>('.projects__sort select');

      if (control === null) {
        throw new Error('The sort select is not rendered.');
      }

      return control;
    }

    async function choose(value: string): Promise<void> {
      const control = select();
      control.value = value;
      control.dispatchEvent(new Event('change'));
      await harness.fixture.whenStable();
    }

    it('offers the four orders frame `4c` draws, with the board default selected', async () => {
      await open();
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      const options = [...select().querySelectorAll('option')];
      expect(options.map((option) => option.value)).toEqual([
        'updated',
        'created',
        'name',
        'favorites',
      ]);
      // "Favourites first", not the board's "Pinned": device-local pins were never built
      // and every other surface in the app says favourite.
      expect(options.map((option) => option.textContent?.trim())).toEqual([
        'Last updated',
        'Recently created',
        'Alphabetical',
        'Favourites first',
      ]);
      expect(select().value).toBe('updated');
    });

    it('carries a hidden label, because the value is not what the control does', async () => {
      await open();
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      expect(element().querySelector('.projects__sort .visually-hidden')?.textContent?.trim()).toBe(
        'Sort projects',
      );
    });

    it('writes the chosen order to the URL and re-runs the query', async () => {
      await open();
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      await choose('name');

      expect(TestBed.inject(Router).url).toBe('/projects?sort=name');
      const reordered = expectRequest();
      expect(reordered.request.params.get('sort')).toBe('name');
      expect(reordered.request.params.get('dir')).toBe('asc');
      reordered.flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      // `[selected]` on the options is uncontrolled: the browser moves the value, and
      // Angular writes it back only when the bound expression changes. Pinned so a path
      // that moves the DOM without moving `sort()` cannot leave the control describing an
      // order the grid is not in.
      expect(select().value).toBe('name');
    });

    it('keeps the term when the order changes, because the two parameters are merged', async () => {
      await open('/projects?name=helios');
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      await choose('favorites');

      expect(TestBed.inject(Router).url).toBe('/projects?name=helios&sort=favorites');
      const reordered = expectRequest();
      expect(reordered.request.params.get('name')).toBe('helios');
      expect(reordered.request.params.get('sort')).toBe('favorite');
      reordered.flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();
    });

    it('removes the key rather than spelling out the default it returns to', async () => {
      await open('/projects?sort=name');
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      await choose('updated');

      expect(TestBed.inject(Router).url).toBe('/projects');
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();
    });

    it('pushes when an order is turned on, so Back restores the unsorted grid', async () => {
      await open();
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      await choose('name');
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      TestBed.inject(Location).back();
      await new Promise((resolve) => setTimeout(resolve, 0));
      await harness.fixture.whenStable();

      expect(TestBed.inject(Router).url).toBe('/projects');
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      // The control follows the URL back, or a reader who pressed Back would be looking
      // at the default grid under a label that still said Alphabetical.
      expect(select().value).toBe('updated');
    });

    it('replaces between two orders, so arrowing through the select does not stack history', async () => {
      // A closed `<select>` fires `change` on every arrow key. Pushing each one would bury
      // the unsorted grid three entries deep behind orders nobody chose.
      await open('/projects?sort=created');
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();

      await choose('name');
      expectRequest().flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();
      expect(TestBed.inject(Router).url).toBe('/projects?sort=name');

      TestBed.inject(Location).back();
      await new Promise((resolve) => setTimeout(resolve, 0));
      await harness.fixture.whenStable();

      // Back over a replaced entry leaves the screen, rather than stepping to `created`.
      expect(TestBed.inject(Router).url).not.toBe('/projects?sort=created');
    });

    it('issues exactly one request for a deep link that already names an order', async () => {
      await open('/projects?sort=favorites');

      const request = expectRequest();
      expect(request.request.params.get('sort')).toBe('favorite');
      expect(select().value).toBe('favorites');

      request.flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();
    });

    it('falls back to the default when the URL names an order that does not exist', async () => {
      // The control can never display a value the URL contradicts, and the server never
      // sees a `sort=` it would answer with a 400 the reader could not explain.
      await open('/projects?sort=zzz');

      const request = expectRequest();
      expect(request.request.params.get('sort')).toBe('updated');
      expect(select().value).toBe('updated');

      request.flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();
    });

    it("stays enabled past the load ceiling, because the order is the server's", async () => {
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

      // Frame `4d` disables it. That state described a client-side sort over a partial
      // set; the 500 on screen are now the correct first 500 in this order.
      expect(select().disabled).toBe(false);
    });
  });
});
