import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { getByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { projectEvents } from '@core/events/project-events';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { dialogNamed } from '@testing/overlays';
import { projectDetailFixture } from '@testing/projects';
import { InstructionsPanel } from './instructions-panel';
import { ProjectStore } from './project-store';

const PROJECT = projectDetailFixture({
  name: 'Helios',
  description: 'Cutover work',
  instructions: 'Cite Jira keys.',
});
const PROJECT_URL = `${TEST_API_BASE_URL}/api/projects/${PROJECT.id}`;

describe('InstructionsPanel', () => {
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
    return getByRole<HTMLTextAreaElement>(host(), 'textbox', { name: 'Standing instructions' });
  }

  function button(name: string): HTMLButtonElement {
    return getByRole<HTMLButtonElement>(host(), 'button', { name });
  }

  function confirmation(): HTMLDialogElement {
    return dialogNamed('Discard unsaved instructions?') as HTMLDialogElement;
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
    // The listing shape omits instructions, so only the detail route could have filled this.
    expect(field().value).toBe('Cite Jira keys.');
  });

  it('settles only once the server confirms, never optimistically', async () => {
    await edit('New rules.');

    getByRole(host(), 'button', { name: 'Save' }).click();
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

    expect(confirmation().open).toBe(true);

    getByRole(confirmation(), 'button', { name: 'Discard' }).click();
    await fixture.whenStable();

    await expect(decision).resolves.toBe(true);
    // Reset before the navigation is released, so the guard cannot fire a second time.
    expect(field().value).toBe('Cite Jira keys.');
  });

  it('keeps the reader on the panel when they choose to keep editing', async () => {
    await edit('Half-typed');

    const decision = panel.confirmDiscard();
    await fixture.whenStable();

    getByRole(confirmation(), 'button', { name: 'Keep editing' }).click();
    await fixture.whenStable();

    await expect(decision).resolves.toBe(false);
    expect(field().value).toBe('Half-typed');
  });

  it('treats Escape as keep-editing rather than as a silent discard', async () => {
    await edit('Half-typed');

    const decision = panel.confirmDiscard();
    await fixture.whenStable();

    confirmation().dispatchEvent(new Event('close'));
    await fixture.whenStable();

    await expect(decision).resolves.toBe(false);
    expect(field().value).toBe('Half-typed');
  });

  it('keeps an unsaved edit when a favourite replaces the project object', async () => {
    await edit('Half-typed');

    TestBed.inject(Dispatcher).dispatch(
      projectEvents.favorited({ id: PROJECT.id, isFavorite: true }),
    );
    await fixture.whenStable();

    expect(field().value).toBe('Half-typed');
  });

  it('keeps an unsaved edit when a rename echoes the same instructions', async () => {
    await edit('Half-typed');

    TestBed.inject(Dispatcher).dispatch(projectEvents.updated({ ...PROJECT, name: 'Helios 2' }));
    await fixture.whenStable();

    expect(field().value).toBe('Half-typed');
  });

  it('treats trailing whitespace as no change, since the save trims it', async () => {
    await edit('Cite Jira keys.  ');

    expect(button('Save').getAttribute('aria-disabled')).toBe('true');
    expect(button('Cancel').getAttribute('aria-disabled')).toBe('true');
  });

  it('keeps focus on Save and the field read-only through the round trip', async () => {
    document.body.append(host());
    await edit('New rules.');
    const save = button('Save');
    save.focus();

    save.click();
    await fixture.whenStable();

    expect(document.activeElement).toBe(save);
    expect(field().readOnly).toBe(true);
    expect(field().disabled).toBe(false);
    expect(save.getAttribute('aria-disabled')).toBe('true');

    expectSave().flush({ ...PROJECT, instructions: 'New rules.' });
    await fixture.whenStable();

    expect(document.activeElement).toBe(save);
    expect(field().readOnly).toBe(false);
    host().remove();
  });

  it('does nothing when an inert Save or Cancel is pressed', async () => {
    button('Save').click();
    button('Cancel').click();
    await fixture.whenStable();

    backend.expectNone((request) => request.method === 'PUT');
    expect(field().value).toBe('Cite Jira keys.');
  });

  it('reverts without leaving the panel when Cancel is pressed', async () => {
    await edit('Half-typed');

    getByRole(host(), 'button', { name: 'Cancel' }).click();
    await fixture.whenStable();

    expect(field().value).toBe('Cite Jira keys.');
    expect(TestBed.inject(ProjectActionsStore).formBusy()).toBe(false);
  });
});
