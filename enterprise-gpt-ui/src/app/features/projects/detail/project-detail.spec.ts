import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TokenService } from '@core/auth/token-service';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { COMPOSER_UPLOADS, ComposerUploads } from '@core/documents/composer-uploads';
import { UploadStore } from '@core/documents/upload-store';
import { projectEvents } from '@core/events/project-events';
import { SessionStore } from '@core/session/session-store';
import { ConversationDto } from '@domain/api/conversation';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { projectDetailFixture, projectDocumentFixture } from '@testing/projects';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import projectDetailRoutes from './project-detail.routes';

const PROJECT = projectDetailFixture({ name: 'Helios', instructions: 'Cite Jira keys.' });
const PROJECT_URL = `${TEST_API_BASE_URL}/api/projects/${PROJECT.id}`;
const DOCUMENTS_URL = `${PROJECT_URL}/documents`;
const CONVERSATIONS_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

describe('ProjectDetail (US-904, US-905, US-906)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let uploadXhr: FakeUploadXhrQueue;

  beforeEach(async () => {
    installDialogPolyfill();
    uploadXhr = new FakeUploadXhrQueue();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideFakeUploadXhr(uploadXhr),
        { provide: TokenService, useValue: { getToken: vi.fn().mockResolvedValue('token-1') } },
        { provide: SessionStore, useValue: { canUploadFiles: signal(true) } },
        // The composer reads the catalogue through `TurnSettingsStore`; in the app
        // `SessionBootstrap` fills it, and loading it here would put a second request
        // in front of every assertion.
        {
          provide: ModelCatalogStore,
          useValue: {
            models: signal([]),
            error: signal(null),
            isFulfilled: signal(true),
            defaultModel: signal(null),
            ensureLoaded: () => Promise.resolve(true),
          },
        },
        provideRouter(
          [
            { path: 'projects', children: [{ path: ':projectId', children: projectDetailRoutes }] },
            { path: 'chat/:conversationId', children: [] },
          ],
          withComponentInputBinding(),
        ),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    TestBed.inject(Router).setUpLocationChangeListener();
  });

  afterEach(() => {
    backend.verify();
  });

  function host(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  /**
   * The upload store from `ProjectDetail`'s own element injector.
   *
   * `TestBed.inject` would not find it: it is provided on the component, and the routed
   * files panel resolves it up the element-injector chain. This is the *project's*
   * store — the composer's is a separate instance under `COMPOSER_UPLOADS`.
   */
  function uploads(): InstanceType<typeof UploadStore> {
    return harness.routeDebugElement!.injector.get(UploadStore);
  }

  /** The composer's own store on this screen, targeted at no parent yet. */
  function composerUploads(): ComposerUploads {
    return harness.routeDebugElement!.injector.get(COMPOSER_UPLOADS);
  }

  /** Lets the router's navigation pipeline run far enough to reach a guard. */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
  }

  /**
   * Advances a macrotask and renders, without waiting for stability.
   *
   * `whenStable()` cannot be used while a `canDeactivate` guard is holding a
   * navigation: the guard is *deliberately* unresolved, so the application is never
   * stable and the await parks until the test times out.
   */
  async function pump(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    TestBed.tick();
  }

  /**
   * Types into the instructions field by id.
   *
   * By id and not `querySelector('textarea')`: the composer sits above the tab strip
   * and its prompt box comes first in DOM order, so the loose selector types into the
   * wrong control and the guard never sees an edit.
   */
  async function editInstructions(text: string): Promise<void> {
    const field = host().querySelector('#project-instructions') as HTMLTextAreaElement;
    field.value = text;
    field.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();
  }

  async function open(path = '', conversations: ConversationDto[] = []): Promise<void> {
    await harness.navigateByUrl(`/projects/${PROJECT.id}${path}`);
    backend.expectOne(PROJECT_URL).flush(PROJECT);
    backend.expectOne(DOCUMENTS_URL).flush([projectDocumentFixture({ name: 'plan.pdf' })]);
    // US-908's filtered request, issued from the detail screen rather than its panel so
    // the tab strip's count is right from whichever tab the reader lands on.
    backend
      .expectOne((request) => request.url === CONVERSATIONS_URL)
      .flush(conversationPage(conversations, { pageSize: 25 }));
    await harness.fixture.whenStable();

    // The composer's own lazy read of the deployment's supported extensions, memoized
    // for the session and gated on the upload permission.
    for (const request of backend.match(`${TEST_API_BASE_URL}/api/documents/file-extensions`)) {
      request.flush(['.pdf', '.docx', '.md']);
    }
    await harness.fixture.whenStable();
  }

  it('renders the header, the composer and a tab strip carrying both counts', async () => {
    await open('', [conversationFixture(), conversationFixture()]);

    expect(host().querySelector('.detail__name')?.textContent?.trim()).toBe('Helios');
    // The composer is the shared component; what differs is the host it sends into.
    expect(host().querySelector('app-composer')).not.toBeNull();

    const tabs = [...host().querySelectorAll('.nav-link')].map((tab) =>
      tab.textContent?.replace(/\s+/g, ' ').trim(),
    );
    // Frame 4e's three tabs, in the board's order, each carrying its own count.
    expect(tabs).toEqual(['Instructions', 'Files 1', 'Conversations 2']);
  });

  it('counts a project’s conversations from whichever tab the reader is on (US-908)', async () => {
    // The store is provided on the route rather than on the panel, so the strip is
    // right while the reader stands on Instructions and the panel does not exist.
    await open('/instructions', [conversationFixture()]);

    expect(host().querySelector('app-project-conversations')).toBeNull();
    const counts = [...host().querySelectorAll('.detail__tab-count')].map((span) =>
      span.textContent?.trim(),
    );
    expect(counts).toEqual(['1', '1']);
  });

  it('resolves the upload store through the outlet, so the files panel shares one', async () => {
    await open('/files');

    // `UploadStore` is provided on this component and the routed panel resolves it up
    // the element-injector chain, so switching tabs mid-upload does not tear the
    // transfer down.
    uploads().attach([new File(['x'], 'notes.pdf', { type: 'application/pdf' })]);
    await settle();

    expect(uploadXhr.last.url).toBe(`${TEST_API_BASE_URL}/api/documents/projects/${PROJECT.id}`);
    // The chip renders in the panel, from the instance the parent provided.
    expect(host().querySelector('app-attachment-chip')).not.toBeNull();

    uploadXhr.last.respond(202, { id: 'job-1' });
    await harness.fixture.whenStable();

    // The staged poll is EP-8's and already covered; it is drained here only so the
    // backend verification at the end of the test has nothing outstanding.
    await new Promise((resolve) => setTimeout(resolve, 600));
    backend
      .expectOne((candidate) => candidate.url.includes('upload-status'))
      .flush(jobStatus('Failed', null));
    await harness.fixture.whenStable();
  });

  it('keeps the composer’s files off the project', async () => {
    await open('/files');

    const composer = composerUploads();
    expect(composer).not.toBe(uploads());

    composer.attach([new File(['x'], 'notes.pdf', { type: 'application/pdf' })]);
    await settle();

    // The composer starts a conversation; a file attached there belongs to that
    // conversation, so it waits for one to exist rather than joining the project.
    expect(uploadXhr.requests).toHaveLength(0);
    expect(composer.pendingFiles().map((file) => file.name)).toEqual(['notes.pdf']);
  });

  it('turns a finished upload into a row even while another tab is open', async () => {
    // Instructions, not Files: the bridge lives on this component precisely so a file
    // dropped on the composer from any tab still becomes a row and still moves the
    // count on the tab strip.
    await open('/instructions');

    uploads().attach([new File(['x'], 'notes.pdf', { type: 'application/pdf' })]);
    await settle();

    uploadXhr.last.respond(202, { id: 'job-1' });
    await harness.fixture.whenStable();

    // `UploadStatusClient` waits out its first staged delay (500 ms) before reading, so
    // nothing is on the wire until then.
    await new Promise((resolve) => setTimeout(resolve, 600));
    await harness.fixture.whenStable();

    backend
      .expectOne((candidate) => candidate.url.includes('upload-status'))
      .flush(jobStatus('Succeeded', projectDocumentFixture().id));
    await harness.fixture.whenStable();

    // The list is re-read, because the upload route answers with a job id and never a
    // document — the name, size and date can only come from asking again.
    backend
      .expectOne(DOCUMENTS_URL)
      .flush([projectDocumentFixture({ name: 'plan.pdf' }), projectDocumentFixture()]);
    await harness.fixture.whenStable();

    expect(host().querySelector('.detail__tab-count')?.textContent?.trim()).toBe('2');
    expect(uploads().hasAttachments()).toBe(false);
  });

  it('leaves for the grid when the project is deleted under it', async () => {
    await open();

    TestBed.inject(Dispatcher).dispatch(projectEvents.deleted(PROJECT.id));
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/projects');
  });

  it('does not let the unsaved-instructions guard trap a reader on a deleted project', async () => {
    await open('/instructions');

    await editInstructions('Half-typed');

    TestBed.inject(Dispatcher).dispatch(projectEvents.deleted(PROJECT.id));
    await harness.fixture.whenStable();

    // No modal, and the navigation completes: there is nothing left to save the edit
    // into, and `deleted` is a latch — a cancelled navigation would never be retried.
    expect(TestBed.inject(Router).url).toBe('/projects');
  });

  it('warns before a tab change loses an instructions edit', async () => {
    await open('/instructions');

    await editInstructions('Half-typed');

    const navigation = TestBed.inject(Router).navigateByUrl(`/projects/${PROJECT.id}/files`);
    await pump();

    // Through the router, not by calling the panel: the guard is what has to fire, and
    // it fires on a tab change as well as on leaving the project.
    expect(host().querySelector('dialog')?.open).toBe(true);

    const discard = host().querySelector<HTMLButtonElement>('dialog .btn-danger');
    expect(discard).not.toBeNull();
    discard?.click();

    await expect(navigation).resolves.toBe(true);
    await settle();

    expect(TestBed.inject(Router).url).toBe(`/projects/${PROJECT.id}/files`);
  });

  describe('favourite (US-909)', () => {
    function star(): HTMLButtonElement | null {
      return host().querySelector<HTMLButtonElement>('.detail__favorite');
    }

    it('names the direction it would take, and issues the set on press', async () => {
      await open();

      expect(star()?.getAttribute('aria-label')).toBe('Favourite Helios');
      star()?.click();
      await harness.fixture.whenStable();

      const request = backend.expectOne(`${PROJECT_URL}/favorite`);
      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({ isFavorite: true });
      // Not optimistic, like every other project write: the label holds until the 204.
      expect(star()?.getAttribute('aria-label')).toBe('Favourite Helios');
      expect(star()?.getAttribute('aria-disabled')).toBe('true');

      request.flush(null, { status: 204, statusText: 'No Content' });
      await harness.fixture.whenStable();

      expect(star()?.getAttribute('aria-label')).toBe('Unfavourite Helios');
      expect(star()?.getAttribute('aria-disabled')).toBeNull();
    });

    it('keeps the standing instructions when the flag lands', async () => {
      // The whole reason `favorited` is its own event carrying two fields: an `updated`
      // with a synthesized DTO would overwrite `instructions` with a value nothing
      // fetched, and this screen is where that would be visible.
      await open();

      TestBed.inject(Dispatcher).dispatch(
        projectEvents.favorited({ id: PROJECT.id, isFavorite: true }),
      );
      await harness.fixture.whenStable();

      expect(star()?.getAttribute('aria-label')).toBe('Unfavourite Helios');
      expect(host().querySelector<HTMLTextAreaElement>('#project-instructions')?.value).toBe(
        'Cite Jira keys.',
      );
    });

    it('ignores a favourite announced for a different project', async () => {
      await open();

      TestBed.inject(Dispatcher).dispatch(
        projectEvents.favorited({ id: projectDetailFixture().id, isFavorite: true }),
      );
      await harness.fixture.whenStable();

      expect(star()?.getAttribute('aria-label')).toBe('Favourite Helios');
    });
  });
});

function jobStatus(state: string, documentId: string | null) {
  return {
    id: 'job-1',
    state,
    status: state,
    progress: documentId === null ? 40 : 100,
    message: null,
    completedUnits: null,
    totalUnits: null,
    documentId,
    errorMessage: null,
    updatedAt: '2026-08-14T09:00:00+00:00',
  };
}
