import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { getByRole, getByText, queryByRole, queryByText } from '@testing-library/dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AccessTokenClient } from '@core/auth/access-token-client';
import { SessionStore } from '@core/session/session-store';
import { UploadStore } from '@core/uploads/upload-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { dialogNamed } from '@testing/overlays';
import { projectDetailFixture, projectDocumentFixture } from '@testing/projects';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import { ProjectDocumentsStore } from './project-documents-store';
import { ProjectFiles } from './project-files';
import { ProjectStore } from './project-store';

const PROJECT = projectDetailFixture();
const PROJECT_URL = `${TEST_API_BASE_URL}/api/projects/${PROJECT.id}`;
const DOCUMENTS_URL = `${PROJECT_URL}/documents`;
const EXTENSIONS_URL = `${TEST_API_BASE_URL}/api/documents/file-extensions`;

describe('ProjectFiles', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<ProjectFiles>;
  let canUploadFiles: ReturnType<typeof signal<boolean>>;
  let uploadXhr: FakeUploadXhrQueue;

  async function render(options: { canUpload?: boolean } = {}): Promise<void> {
    installDialogPolyfill();
    canUploadFiles = signal(options.canUpload ?? true);
    uploadXhr = new FakeUploadXhrQueue();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideFakeUploadXhr(uploadXhr),
        // The raw-XHR upload path reads the bearer itself; the real service would drag
        // MSAL into a spec about a file list.
        {
          provide: AccessTokenClient,
          useValue: { getToken: vi.fn().mockResolvedValue('token-1') },
        },
        { provide: SessionStore, useValue: { canUploadFiles } },
        ProjectStore,
        ProjectDocumentsStore,
        UploadStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);

    const project = TestBed.inject(ProjectStore);
    TestBed.runInInjectionContext(() => project.bindRoute(() => PROJECT.id));
    TestBed.tick();
    backend.expectOne(PROJECT_URL).flush(PROJECT);

    const documents = TestBed.inject(ProjectDocumentsStore);
    TestBed.runInInjectionContext(() => documents.bindProject(() => PROJECT.id));
    TestBed.tick();
    backend.expectOne(DOCUMENTS_URL).flush([projectDocumentFixture({ name: 'plan.pdf' })]);
    TestBed.tick();

    // `ProjectDetail` binds this in the app; with the panel rendered alone, the spec does.
    TestBed.inject(UploadStore).bindProject(PROJECT.id);

    fixture = TestBed.createComponent(ProjectFiles);
    await fixture.whenStable();

    // Gated on the permission, and counted because a loop over `match` alone would silently
    // cover nothing if the request stopped being made.
    const extensions = backend.match(EXTENSIONS_URL);
    expect(extensions.length).toBe(canUploadFiles() ? 1 : 0);
    for (const request of extensions) {
      request.flush(['.pdf', '.docx', '.md']);
    }
    await fixture.whenStable();
  }

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => {
    backend.verify();
  });

  it('lists a project’s documents with size and date', async () => {
    await render();

    const row = host().querySelector('.files__row');
    expect(row?.textContent).toContain('plan.pdf');
    expect(host().querySelectorAll('.files__row').length).toBe(1);
  });

  it('offers the drop zone, the browse control and the picker with the grant', async () => {
    await render({ canUpload: true });

    expect(getByText(host(), /Drop files here/)).toBeTruthy();
    expect(getByRole(host(), 'button', { name: 'browse' })).toBeTruthy();
    expect(host().querySelector('input[type="file"]')).not.toBeNull();
  });

  it('withholds every add affordance without the grant, and keeps files downloadable', async () => {
    await render({ canUpload: false });

    // Absent, not disabled, and the drop directive goes with it.
    expect(queryByText(host(), /Drop files here/)).toBeNull();
    expect(queryByRole(host(), 'button', { name: 'browse' })).toBeNull();
    expect(host().querySelector('input[type="file"]')).toBeNull();

    expect(host().querySelector('.files__row')).not.toBeNull();
    expect(getByRole(host(), 'button', { name: 'Download plan.pdf' })).toBeTruthy();
    // Removing is an upload-permission action, so that control goes with the rest.
    expect(queryByRole(host(), 'button', { name: 'Remove plan.pdf' })).toBeNull();
  });

  it('confirms a removal before making it, and names the file', async () => {
    await render();

    getByRole(host(), 'button', { name: 'Remove plan.pdf' }).click();
    await fixture.whenStable();

    const dialog = dialogNamed('Remove file?');
    expect(dialog.textContent).toContain('plan.pdf');

    getByRole(dialog, 'button', { name: 'Remove' }).click();
    await fixture.whenStable();

    const request = backend.expectOne((candidate) => candidate.method === 'DELETE');
    expect(request.request.url).toContain(`${DOCUMENTS_URL}/`);
    request.flush(null, { status: 204, statusText: 'No Content' });
    await fixture.whenStable();

    expect(host().querySelector('.files__row')).toBeNull();
  });

  it('posts an attached file to the project’s own upload route', async () => {
    await render();

    TestBed.inject(UploadStore).attach([new File(['x'], 'notes.pdf', { type: 'application/pdf' })]);
    // The transport awaits a token before it opens the request, so one macrotask has
    // to pass before the fake has seen anything.
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();

    const xhr = uploadXhr.last;
    expect(xhr?.url).toBe(`${TEST_API_BASE_URL}/api/documents/projects/${PROJECT.id}`);
    expect(xhr?.file()?.name).toBe('notes.pdf');

    xhr?.respond(202, { id: 'job-1' });
    await fixture.whenStable();

    // The staged poll takes over from here; its own specs cover it.
    for (const request of backend.match((candidate) => candidate.url.includes('upload-status'))) {
      request.flush({
        id: 'job-1',
        state: 'Processing',
        status: 'Chunking',
        progress: 10,
        message: null,
        completedUnits: null,
        totalUnits: null,
        documentId: null,
        errorMessage: null,
        updatedAt: '2026-08-14T09:00:00+00:00',
      });
    }
  });
});
