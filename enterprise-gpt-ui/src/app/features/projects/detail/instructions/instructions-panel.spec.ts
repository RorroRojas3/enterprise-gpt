import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { projectDetailFixture } from '@testing/projects';
import { ProjectStore } from '../project-store';
import { InstructionsPanel } from './instructions-panel';

const PROJECT = projectDetailFixture({
  name: 'Helios',
  description: 'Cutover work',
  instructions: 'Cite Jira keys.',
});
const PROJECT_URL = `${TEST_API_BASE_URL}/api/projects/${PROJECT.id}`;

describe('InstructionsPanel (US-904)', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<InstructionsPanel>;
  let panel: InstructionsPanel;

  beforeEach(async () => {
    installDialogPolyfill();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        ProjectStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);

    const store = TestBed.inject(ProjectStore);
    TestBed.runInInjectionContext(() => store.bindRoute(() => PROJECT.id));
    TestBed.tick();
    backend.expectOne(PROJECT_URL).flush(PROJECT);
    TestBed.tick();

    fixture = TestBed.createComponent(InstructionsPanel);
    panel = fixture.componentInstance;
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  /** `fixture.nativeElement` is `any`, which rejects the generic on `querySelector`. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function field(): HTMLTextAreaElement {
    return host().querySelector('#project-instructions') as HTMLTextAreaElement;
  }

  async function edit(text: string): Promise<void> {
    const textarea = field();
    textarea.value = text;
    textarea.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  function expectSave(): TestRequest {
    return backend.expectOne((request) => request.url === PROJECT_URL && request.method === 'PUT');
  }

  it('seeds the field from the only route that carries instructions', () => {
    // The listing shape omits the column entirely, so a card could never have filled
    // this.
    expect(field().value).toBe('Cite Jira keys.');
  });

  it('settles only once the server confirms, never optimistically', async () => {
    await edit('New rules.');

    host().querySelector<HTMLButtonElement>('.btn-primary')?.click();
    await fixture.whenStable();

    const request = expectSave();
    // Name and description echoed, or a full-representation PUT would clear both.
    expect(request.request.body).toEqual({
      name: 'Helios',
      description: 'Cutover work',
      instructions: 'New rules.',
    });
    expect(host().textContent).toContain('Saving…');

    request.flush({ ...PROJECT, instructions: 'New rules.' });
    await fixture.whenStable();

    expect(TestBed.inject(ProjectStore).project()?.instructions).toBe('New rules.');
    expect(host().textContent).not.toContain('Saving…');
  });

  it('lets a clean panel be left without a modal', async () => {
    await expect(panel.confirmDiscard()).resolves.toBe(true);
  });

  it('blocks a navigation with unsaved edits until the reader decides', async () => {
    await edit('Half-typed');

    const decision = panel.confirmDiscard();
    await fixture.whenStable();

    expect(host().querySelector('dialog')?.open).toBe(true);

    host().querySelector<HTMLButtonElement>('.btn-danger')?.click();
    await fixture.whenStable();

    await expect(decision).resolves.toBe(true);
    // Reset before the navigation is released, so the guard cannot fire a second time
    // against an edit the reader has already agreed to lose.
    expect(field().value).toBe('Cite Jira keys.');
  });

  it('keeps the reader on the panel when they choose to keep editing', async () => {
    await edit('Half-typed');

    const decision = panel.confirmDiscard();
    await fixture.whenStable();

    // Scoped to the dialog: the panel's own Cancel carries the same class and would
    // revert the edit instead of answering the guard.
    host().querySelector<HTMLButtonElement>('dialog .btn-outline-secondary')?.click();
    await fixture.whenStable();

    await expect(decision).resolves.toBe(false);
    expect(field().value).toBe('Half-typed');
  });

  it('treats Escape as keep-editing rather than as a silent discard', async () => {
    await edit('Half-typed');

    const decision = panel.confirmDiscard();
    await fixture.whenStable();

    host().querySelector('dialog')?.dispatchEvent(new Event('close'));
    await fixture.whenStable();

    await expect(decision).resolves.toBe(false);
    expect(field().value).toBe('Half-typed');
  });

  it('reverts without leaving the panel when Cancel is pressed', async () => {
    await edit('Half-typed');

    host()
      .querySelector<HTMLButtonElement>('.instructions__actions .btn-outline-secondary')
      ?.click();
    await fixture.whenStable();

    expect(field().value).toBe('Cite Jira keys.');
    expect(TestBed.inject(ProjectActionsStore).formBusy()).toBe(false);
  });
});
