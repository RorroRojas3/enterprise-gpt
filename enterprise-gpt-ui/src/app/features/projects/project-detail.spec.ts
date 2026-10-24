import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { getAllByRole, getByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConversationDto } from '@core/api/conversation.model';
import { AccessTokenClient } from '@core/auth/access-token-client';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { projectEvents } from '@core/events/project-events';
import { SessionStore } from '@core/session/session-store';
import { COMPOSER_UPLOADS, ComposerUploads } from '@core/uploads/composer-uploads-token';
import { UploadStore } from '@core/uploads/upload-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { dialogNamed } from '@testing/overlays';
import { projectDetailFixture, projectDocumentFixture } from '@testing/projects';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import projectDetailRoutes from './project-detail.routes';

const PROJECT = projectDetailFixture({ name: 'Helios', instructions: 'Cite Jira keys.' });
const PROJECT_URL = `${TEST_API_BASE_URL}/api/projects/${PROJECT.id}`;
const DOCUMENTS_URL = `${PROJECT_URL}/documents`;
const CONVERSATIONS_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

describe('ProjectDetail', () => {
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
        {
          provide: AccessTokenClient,
          useValue: { getToken: vi.fn().mockResolvedValue('token-1') },
        },
        { provide: SessionStore, useValue: { canUploadFiles: signal(true) } },
        // `SessionBootstrap` fills the catalogue in the app; loading it here would put a second
        // request in front of every assertion.
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

  /** From the component's element injector, which `TestBed.inject` cannot reach. */
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

  /** Not `whenStable()`, which never settles while a guard deliberately holds a navigation. */
  /** The header; scoped to it, since the composer below has buttons of its own. */
  function header(): HTMLElement {
    const element = host().querySelector<HTMLElement>('header');
    expect(element).not.toBeNull();
    return element!;
  }

  /** The tab strip's link names, counts included, whitespace collapsed. */
  function tabNames(): (string | undefined)[] {
    const nav = getByRole(host(), 'navigation', { name: 'Project sections' });
    return getAllByRole(nav, 'link').map((tab) => tab.textContent?.replace(/\s+/g, ' ').trim());
  }

  async function pump(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    TestBed.tick();
  }

  /** By id: the composer's prompt box precedes it in DOM order, so a bare `textarea` misses. */
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
    // Issued by the detail screen, not the panel, so the tab count is right from any tab.
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

    // The name is the rename control, styled as the heading.
    expect(getByRole(header(), 'button', { name: 'Helios' })).toBeTruthy();
    // The composer is the shared component; what differs is the host it sends into.
    expect(host().querySelector('app-composer')).not.toBeNull();

    // Three tabs, in order, each with its own count.
    expect(tabNames()).toEqual(['Instructions', 'Files 1', 'Conversations 2']);
  });

  it('counts a project’s conversations from whichever tab the reader is on', async () => {
    await open('/instructions', [conversationFixture()]);

    expect(host().querySelector('app-project-conversations')).toBeNull();
    expect(tabNames()).toEqual(['Instructions', 'Files 1', 'Conversations 1']);
  });

  it('resolves the upload store through the outlet, so the files panel shares one', async () => {
    await open('/files');

    uploads().attach([new File(['x'], 'notes.pdf', { type: 'application/pdf' })]);
    await settle();

    expect(uploadXhr.last.url).toBe(`${TEST_API_BASE_URL}/api/documents/projects/${PROJECT.id}`);
    // The chip renders in the panel, from the instance the parent provided.
    expect(host().querySelector('app-attachment-chip')).not.toBeNull();

    uploadXhr.last.respond(202, { id: 'job-1' });
    await harness.fixture.whenStable();

    // The staged poll has its own specs; drained only so `verify()` finds nothing outstanding.
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

    // A composer file belongs to the conversation the prompt will create, not the project.
    expect(uploadXhr.requests).toHaveLength(0);
    expect(composer.pendingFiles().map((file) => file.name)).toEqual(['notes.pdf']);
  });

  it('turns a finished upload into a row even while another tab is open', async () => {
    // On Instructions, not Files: the bridge must turn uploads into rows from any tab.
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

    // Re-read, because the upload route answers with a job id, not a document.
    backend
      .expectOne(DOCUMENTS_URL)
      .flush([projectDocumentFixture({ name: 'plan.pdf' }), projectDocumentFixture()]);
    await harness.fixture.whenStable();

    expect(tabNames()[1]).toBe('Files 2');
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
    const dialog = dialogNamed('Discard unsaved instructions?') as HTMLDialogElement;
    expect(dialog.open).toBe(true);

    getByRole(dialog, 'button', { name: 'Discard' }).click();

    await expect(navigation).resolves.toBe(true);
    await settle();

    expect(TestBed.inject(Router).url).toBe(`/projects/${PROJECT.id}/files`);
  });

  describe('favourite', () => {
    function star(): HTMLButtonElement | null {
      return getByRole<HTMLButtonElement>(header(), 'button', {
        name: /^(Unf|F)avourite Helios$/,
      });
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
      // Why `favorited` carries two fields: a synthesized `updated` DTO would overwrite
      // `instructions` with a value nothing fetched.
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
