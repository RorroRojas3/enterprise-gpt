import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { PROJECT_FIELD_LENGTHS } from '@domain/api/project';
import { DUPLICATE_NAME_MESSAGE } from '@core/projects/duplicate-name';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { projectDetailFixture, projectFixture } from '@testing/projects';
import { ProjectFormDialog } from './project-form-dialog';

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

const DUPLICATE_NAME_PROBLEM = {
  ...PROBLEM_FIXTURES.validationError,
  instance: '/api/projects',
  errors: { Name: ["A project named 'RFP Responses' already exists."] },
};

describe('ProjectFormDialog (US-903)', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<ProjectFormDialog>;
  let actions: InstanceType<typeof ProjectActionsStore>;

  beforeEach(async () => {
    installDialogPolyfill();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    actions = TestBed.inject(ProjectActionsStore);

    fixture = TestBed.createComponent(ProjectFormDialog);
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function nameField(): HTMLInputElement {
    return host().querySelector('#project-form-name') as HTMLInputElement;
  }

  function instructionsField(): HTMLTextAreaElement {
    return host().querySelector('#project-form-instructions') as HTMLTextAreaElement;
  }

  async function type(field: HTMLInputElement | HTMLTextAreaElement, text: string): Promise<void> {
    field.value = text;
    field.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  it('stays shut until the store opens it', () => {
    expect(host().querySelector('dialog')?.open).toBeFalsy();
  });

  it('opens empty for a create, and labels itself for one', async () => {
    actions.beginCreate();
    await fixture.whenStable();

    expect(host().querySelector('dialog')?.open).toBe(true);
    expect(host().textContent).toContain('New project');
    expect(host().textContent).toContain('Create project');
    expect(nameField().value).toBe('');
    // Autofocus is on the field, not on a button — this is a form, not a confirmation.
    expect(nameField().hasAttribute('data-modal-autofocus')).toBe(true);
  });

  it('seeds from the project on an edit, instructions included', async () => {
    actions.beginEdit(projectDetailFixture({ name: 'Helios', instructions: 'Cite Jira keys.' }));
    await fixture.whenStable();

    expect(host().textContent).toContain('Edit project');
    expect(host().textContent).toContain('Save');
    expect(nameField().value).toBe('Helios');
    expect(instructionsField().value).toBe('Cite Jira keys.');
  });

  it('re-seeds when the project arrives one round trip after the dialog opened', async () => {
    const summary = projectFixture({ name: 'Helios' });

    // The grid's kebab has only the listing shape, which carries no `instructions`
    // key at all, so the dialog opens on the click and fills in when the detail lands.
    actions.beginEdit(summary);
    await fixture.whenStable();

    expect(host().querySelector('dialog')?.open).toBe(true);
    expect(nameField().disabled).toBe(true);

    backend
      .expectOne(`${PROJECTS_URL}/${summary.id}`)
      .flush(projectDetailFixture({ ...summary, instructions: 'Cite Jira keys.' }));
    await fixture.whenStable();

    expect(nameField().disabled).toBe(false);
    expect(instructionsField().value).toBe('Cite Jira keys.');
  });

  it('derives maxlength from the schema rather than a literal', async () => {
    actions.beginCreate();
    await fixture.whenStable();

    // NG8022: the [formField] directive owns the constraint attributes, so a literal
    // `maxlength` in the template would not compile.
    expect(nameField().getAttribute('maxlength')).toBe(String(PROJECT_FIELD_LENGTHS.name));
  });

  it('refuses to submit an empty name', async () => {
    actions.beginCreate();
    await fixture.whenStable();

    const submit = host().querySelector<HTMLButtonElement>('button[type="submit"]');
    expect(submit?.disabled).toBe(true);

    await type(nameField(), 'Helios');
    expect(submit?.disabled).toBe(false);
  });

  it('renders the board’s duplicate-name copy, not the server’s wording', async () => {
    actions.beginCreate();
    await fixture.whenStable();
    await type(nameField(), 'RFP Responses');

    host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    await fixture.whenStable();

    backend
      .expectOne((request) => request.url === PROJECTS_URL && request.method === 'POST')
      .flush(DUPLICATE_NAME_PROBLEM, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();

    const errors = host().querySelector('#project-form-name-errors');
    expect(errors?.getAttribute('role')).toBe('alert');
    expect(errors?.textContent).toContain(DUPLICATE_NAME_MESSAGE);
    // The server's own sentence is replaced, not appended.
    expect(errors?.textContent).not.toContain('already exists');
    expect(nameField().classList.contains('is-invalid')).toBe(true);
  });

  it('clears the inline message on the next keystroke, with no imperative reset', async () => {
    actions.beginCreate();
    await fixture.whenStable();
    await type(nameField(), 'RFP Responses');

    host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    await fixture.whenStable();
    backend
      .expectOne((request) => request.method === 'POST')
      .flush(DUPLICATE_NAME_PROBLEM, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();

    expect(host().querySelector('#project-form-name-errors')).not.toBeNull();

    // Signal Forms has no `setErrors` to clear: the rule reports the verdict only while
    // the field still holds the exact name the server rejected.
    await type(nameField(), 'RFP Responses 2026');

    expect(host().querySelector('#project-form-name-errors')).toBeNull();
  });

  it('closes and forgets its fields when cancelled', async () => {
    actions.beginEdit(projectDetailFixture({ name: 'Helios' }));
    await fixture.whenStable();

    actions.cancelForm();
    await fixture.whenStable();

    expect(host().querySelector('dialog')?.open).toBe(false);

    actions.beginCreate();
    await fixture.whenStable();

    // The component is mounted once and lives as long as the projects area, so a
    // previous open must not leak into the next one.
    expect(nameField().value).toBe('');
  });
});
