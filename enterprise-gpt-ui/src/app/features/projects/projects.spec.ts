import { Location } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { getAllByRole, getByRole, queryByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { projectFixture, projectPage } from '@testing/projects';
import { PROJECT_LIST_PAGE_SIZE } from './project-list-store';
import { Projects } from './projects';

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

/**
 * `SearchInput`'s 300 ms debounce plus headroom for a loaded runner. Real timers: a fake clock
 * also stalls the scheduler `whenStable()` awaits.
 */
const DEBOUNCE_MS = 450;

describe('Projects', () => {
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
    // The harness never starts the router's popstate listener, so Back would move only
    // `Location`.
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

  function searchField(): HTMLInputElement {
    return getByRole<HTMLInputElement>(element(), 'searchbox', { name: 'Search projects by name' });
  }

  /** A card's star, named by the direction it would take. */
  function star(name: string): HTMLButtonElement {
    return getByRole<HTMLButtonElement>(element(), 'button', {
      name: new RegExp(`^(Unf|F)avourite ${name}$`),
    });
  }

  async function type(text: string): Promise<void> {
    const field = searchField();
    field.value = text;
    field.dispatchEvent(new Event('input'));
    await new Promise((resolve) => setTimeout(resolve, DEBOUNCE_MS));
    await harness.fixture.whenStable();
  }

  it('issues exactly one request for a deep link that already carries a term', async () => {
    await open('/projects?name=helios');

    // Why `bindQuery` takes a computation: values would fire unfiltered, then filtered.
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

    getByRole(element().querySelector<HTMLElement>('app-empty-state')!, 'button', {
      name: 'New project',
    }).click();
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

    const cards = getAllByRole(element(), 'article');
    expect(cards.length).toBe(2);
    for (const card of cards) {
      expect(getByRole(card, 'button', { name: /^(Unf|F)avourite / })).toBeTruthy();
      expect(getByRole(card, 'button', { name: /^Actions for / })).toBeTruthy();
    }
  });

  it('stars a project through its own route and reflects the server-confirmed flag', async () => {
    await open();
    const project = projectFixture({ name: 'Helios' });
    expectRequest().flush(projectPage([project]));
    await harness.fixture.whenStable();

    expect(star('Helios').getAttribute('aria-label')).toBe('Favourite Helios');
    star('Helios').click();
    await harness.fixture.whenStable();

    const request = backend.expectOne(`${PROJECTS_URL}/${project.id}/favorite`);
    expect(request.request.method).toBe('PUT');
    // A set, not a toggle: the body carries the state being asked for.
    expect(request.request.body).toEqual({ isFavorite: true });

    // Nothing moves until the 204 — every project write here is confirm-then-render.
    expect(star('Helios').getAttribute('aria-label')).toBe('Favourite Helios');

    request.flush(null, { status: 204, statusText: 'No Content' });
    await harness.fixture.whenStable();

    expect(star('Helios').getAttribute('aria-label')).toBe('Unfavourite Helios');
  });

  it('opens the create dialog from the toolbar', async () => {
    await open();
    expectRequest().flush(projectPage([projectFixture()]));
    await harness.fixture.whenStable();

    getByRole(element(), 'button', { name: 'New project' }).click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(ProjectActionsStore).formMode()).toBe('create');
  });

  describe('paging', () => {
    /** Named "Loading…" while a page is on the wire, so either label finds it. */
    function loadMoreButton(): HTMLButtonElement | null {
      return queryByRole<HTMLButtonElement>(element(), 'button', {
        name: /^(Load more|Loading…)$/,
      });
    }

    function cards(): HTMLElement[] {
      return [...element().querySelectorAll<HTMLElement>('app-project-card')];
    }

    /** Opens with a full first page that leaves more behind. */
    async function openPaged(totalCount = 60): Promise<void> {
      await open();
      expectRequest().flush(
        projectPage(
          Array.from({ length: PROJECT_LIST_PAGE_SIZE }, (_, index) =>
            projectFixture({ name: `Project ${index}` }),
          ),
          { totalCount, pageSize: PROJECT_LIST_PAGE_SIZE },
        ),
      );
      await harness.fixture.whenStable();
    }

    it('renders one page of cards from one request, not a drained set', async () => {
      await openPaged(1284);

      expect(cards()).toHaveLength(PROJECT_LIST_PAGE_SIZE);
      backend.verify();
    });

    it('appends the next page in server order', async () => {
      await openPaged();

      loadMoreButton()!.click();
      await harness.fixture.whenStable();

      const request = expectRequest();
      expect(request.request.params.get('skip')).toBe(String(PROJECT_LIST_PAGE_SIZE));
      request.flush(
        projectPage([projectFixture({ name: 'Project 25' })], {
          totalCount: 60,
          pageSize: PROJECT_LIST_PAGE_SIZE,
          skip: PROJECT_LIST_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      const names = cards().map((card) => getByRole(card, 'link').textContent?.trim());
      expect(names).toHaveLength(PROJECT_LIST_PAGE_SIZE + 1);
      expect(names.at(0)).toBe('Project 0');
      expect(names.at(-1)).toBe('Project 25');
    });

    it('removes the control rather than disabling it once everything is loaded', async () => {
      await openPaged(PROJECT_LIST_PAGE_SIZE);

      expect(loadMoreButton()).toBeNull();
    });

    it('marks the control busy while a page is on the wire', async () => {
      await openPaged();

      loadMoreButton()!.click();
      await harness.fixture.whenStable();
      expect(loadMoreButton()?.getAttribute('aria-disabled')).toBe('true');

      expectRequest().flush(
        projectPage([], { totalCount: 60, pageSize: PROJECT_LIST_PAGE_SIZE, skip: 25 }),
      );
      await harness.fixture.whenStable();
    });

    it('marks it busy during a narrowing search too, when pressing it would do nothing', async () => {
      await openPaged();

      await type('helios');
      // The cards and button stay up while the query runs, and `loadMore()` refuses meanwhile.
      expect(loadMoreButton()?.getAttribute('aria-disabled')).toBe('true');

      expectRequest().flush(projectPage([], { pageSize: PROJECT_LIST_PAGE_SIZE }));
      await harness.fixture.whenStable();
    });

    it('fires the same request from the keyboard as from a click', async () => {
      await openPaged();

      const button = loadMoreButton()!;
      button.focus();
      // What Enter and Space do on a real `<button>`: the browser synthesises a click.
      button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      await harness.fixture.whenStable();

      const request = expectRequest();
      expect(request.request.params.get('skip')).toBe(String(PROJECT_LIST_PAGE_SIZE));
      request.flush(
        projectPage([], { totalCount: 60, pageSize: PROJECT_LIST_PAGE_SIZE, skip: 25 }),
      );
      await harness.fixture.whenStable();
    });

    it('moves focus to the last card when the control removes itself', async () => {
      await openPaged(PROJECT_LIST_PAGE_SIZE + 1);

      const button = loadMoreButton()!;
      button.focus();
      button.click();
      await harness.fixture.whenStable();

      expectRequest().flush(
        projectPage([projectFixture({ name: 'Last' })], {
          totalCount: PROJECT_LIST_PAGE_SIZE + 1,
          pageSize: PROJECT_LIST_PAGE_SIZE,
          skip: PROJECT_LIST_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      // Otherwise focus falls to `<body>` and most screen readers reset to the top.
      expect(loadMoreButton()).toBeNull();
      expect(document.activeElement?.textContent?.trim()).toBe('Last');
    });

    it('leaves focus on the control while there is still more to load', async () => {
      await openPaged(100);

      const button = loadMoreButton()!;
      button.focus();
      button.click();
      await harness.fixture.whenStable();

      expectRequest().flush(
        projectPage([projectFixture()], {
          totalCount: 100,
          pageSize: PROJECT_LIST_PAGE_SIZE,
          skip: PROJECT_LIST_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      // Moving focus off a control the reader keeps pressing would be the worse defect.
      expect(document.activeElement).toBe(loadMoreButton());
    });

    it('leaves focus where the reader put it while the page was on the wire', async () => {
      await openPaged(PROJECT_LIST_PAGE_SIZE + 1);

      loadMoreButton()!.focus();
      loadMoreButton()!.click();
      await harness.fixture.whenStable();

      // Yanking the reader out of the field they moved to is worse than the focus loss.
      const field = searchField();
      field.focus();

      expectRequest().flush(
        projectPage([projectFixture({ name: 'Last' })], {
          totalCount: PROJECT_LIST_PAGE_SIZE + 1,
          pageSize: PROJECT_LIST_PAGE_SIZE,
          skip: PROJECT_LIST_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(document.activeElement).toBe(field);
    });

    it('announces the loaded count against the envelope total', async () => {
      await openPaged(312);

      expect(getByRole(element(), 'status').textContent?.trim()).toBe('Showing 25 of 312 projects');
    });
  });

  describe('the sort control', () => {
    function select(): HTMLSelectElement {
      return getByRole<HTMLSelectElement>(element(), 'combobox', { name: 'Sort projects' });
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
      // "Favourites first", not "Pinned": every other surface in the app says favourite.
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

      const label = element().querySelector<HTMLLabelElement>(`label[for="${select().id}"]`);
      expect(label?.textContent?.trim()).toBe('Sort projects');
      expect(label?.classList.contains('visually-hidden')).toBe(true);
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

      // `[selected]` is written back only when the binding changes, so this pins the DOM to
      // `sort()`.
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

      // The control follows the URL back rather than still saying Alphabetical.
      expect(select().value).toBe('updated');
    });

    it('replaces between two orders, so arrowing through the select does not stack history', async () => {
      // A closed `<select>` fires `change` on every arrow key.
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
      // The server never sees a `sort=` it would answer with a 400.
      await open('/projects?sort=zzz');

      const request = expectRequest();
      expect(request.request.params.get('sort')).toBe('updated');
      expect(select().value).toBe('updated');

      request.flush(projectPage([projectFixture()]));
      await harness.fixture.whenStable();
    });

    it("stays enabled over a partially loaded grid, because the order is the server's", async () => {
      await open();
      expectRequest().flush(
        projectPage(
          Array.from({ length: PROJECT_LIST_PAGE_SIZE }, () => projectFixture()),
          { totalCount: 1284, pageSize: PROJECT_LIST_PAGE_SIZE },
        ),
      );
      await harness.fixture.whenStable();

      // The order is the server's, so every page continues the same run.
      expect(select().disabled).toBe(false);
    });
  });
});
