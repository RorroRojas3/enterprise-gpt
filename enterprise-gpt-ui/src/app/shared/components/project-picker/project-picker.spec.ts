import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, ElementRef, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { getByRole, getByText, queryByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConversationDto } from '@shared/models/api/conversation.model';
import { ProjectSummaryDto } from '@shared/models/api/project.model';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { ConversationActionsStore } from '@state/conversation-actions-store';
import { ProjectActionsStore } from '@state/project-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { projectFixture, projectPage } from '@testing/projects';
import { ProjectPicker } from './project-picker';

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;
const PUT_URL = `${TEST_API_BASE_URL}/api/conversations`;

/** A host, because the picker needs a real anchor element to place itself against. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ProjectPicker],
  template: `
    <button #anchor type="button">Move to project</button>
    @if (anchorEl(); as element) {
      <app-project-picker [conversation]="conversation()" [anchor]="element" [(open)]="open" />
    }
  `,
})
class PickerHost {
  readonly anchor = viewChild.required<ElementRef<HTMLButtonElement>>('anchor');
  readonly anchorEl = signal<HTMLElement | null>(null);
  readonly conversation = signal<ConversationDto>(conversationFixture());
  readonly open = signal(true);
}

describe('ProjectPicker', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<PickerHost>;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  /** Drains the lookup store, then mounts the panel already open. */
  async function render(
    projects: ProjectSummaryDto[],
    conversation = conversationFixture(),
  ): Promise<HTMLElement> {
    const lookup = TestBed.inject(ProjectLookupStore);
    lookup.ensureLoaded();
    TestBed.tick();
    backend.expectOne((request) => request.url === PROJECTS_URL).flush(projectPage(projects));
    TestBed.tick();

    fixture = TestBed.createComponent(PickerHost);
    fixture.componentInstance.conversation.set(conversation);
    await fixture.whenStable();
    // The anchor exists only after the first render, hence call sites capture it at the gesture.
    fixture.componentInstance.anchorEl.set(fixture.componentInstance.anchor().nativeElement);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  function options(host: HTMLElement): HTMLButtonElement[] {
    return [...host.querySelectorAll<HTMLButtonElement>('[role="option"]')];
  }

  function rowNamed(host: HTMLElement, text: string): HTMLButtonElement | undefined {
    return [...host.querySelectorAll<HTMLButtonElement>('[data-picker-row]')].find((button) =>
      button.textContent?.includes(text),
    );
  }

  function panel(host: HTMLElement): HTMLElement | null {
    return queryByRole(host, 'dialog', { name: /to a project$/ });
  }

  it('is a dialog holding a listbox, not a menu', async () => {
    const host = await render([projectFixture()]);

    expect(panel(host)?.getAttribute('aria-modal')).toBeNull();
    expect(host.querySelector('[role="listbox"]')).not.toBeNull();
    expect(host.querySelector('[role="menu"]')).toBeNull();
  });

  it('marks the assigned project selected and every other one not', async () => {
    const assigned = projectFixture({ name: 'Data Platform Migration' });
    const other = projectFixture({ name: 'RFP Responses' });
    const host = await render([assigned, other], conversationFixture({ projectId: assigned.id }));

    const rows = options(host);
    expect(rows.map((row) => row.getAttribute('aria-selected'))).toEqual(['true', 'false']);
    expect(rows[0]?.querySelector('use')?.getAttribute('href')).toBe('#bi-folder-fill');
    expect(rows[1]?.querySelector('use')?.getAttribute('href')).toBe('#bi-folder');
  });

  it('filters the list as the reader types, without a request', async () => {
    const host = await render([
      projectFixture({ name: 'Data Platform Migration' }),
      projectFixture({ name: 'RFP Responses' }),
    ]);

    const field = getByRole(host, 'textbox', { name: 'Search projects' }) as HTMLInputElement;
    field.value = 'rfp';
    field.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    // Client-side and case-insensitive, matching the server's `?name=` rule.
    expect(options(host).map((row) => row.textContent?.trim())).toEqual(['RFP Responses']);
    backend.expectNone((request) => request.url === PROJECTS_URL);
  });

  it('moves the conversation into the project that is chosen', async () => {
    const project = projectFixture();
    const conversation = conversationFixture({ name: 'Cutover comms', projectId: null });
    const host = await render([project], conversation);

    options(host)[0]?.click();
    await fixture.whenStable();

    const request = backend.expectOne(PUT_URL);
    expect(request.request.body).toEqual({
      id: conversation.id,
      name: 'Cutover comms',
      projectId: project.id,
    });
    request.flush({ ...conversation, projectId: project.id });
    await fixture.whenStable();

    expect(panel(host)).toBeNull();
    expect(document.activeElement).toBe(fixture.componentInstance.anchor().nativeElement);
  });

  it('offers Remove from project only for a conversation that is in one', async () => {
    // Absent rather than inert.
    const project = projectFixture();
    const standalone = await render([project], conversationFixture({ projectId: null }));
    expect(rowNamed(standalone, 'Remove from project')).toBeUndefined();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });
    backend = TestBed.inject(HttpTestingController);

    const assigned = await render([project], conversationFixture({ projectId: project.id }));
    expect(rowNamed(assigned, 'Remove from project')).toBeDefined();
  });

  it('sends an explicit null when Remove from project is chosen', async () => {
    const project = projectFixture();
    const conversation = conversationFixture({ projectId: project.id });
    const host = await render([project], conversation);

    rowNamed(host, 'Remove from project')?.click();
    await fixture.whenStable();

    const request = backend.expectOne(PUT_URL);
    expect(request.request.body.projectId).toBeNull();
    request.flush({ ...conversation, projectId: null });
  });

  it('hands a project created from the pinned action straight to the move', async () => {
    // The reader should end up where they were, with the new project set.
    const conversation = conversationFixture({ projectId: null });
    const host = await render([], conversation);

    rowNamed(host, 'New project')?.click();
    await fixture.whenStable();

    const actions = TestBed.inject(ProjectActionsStore);
    expect(actions.formMode()).toBe('create');
    // The panel gets out of the way of the modal it just opened.
    expect(panel(host)).toBeNull();

    actions.submitForm({ name: 'Q3 Board Deck', description: '', instructions: '' });
    const created = backend.expectOne(`${TEST_API_BASE_URL}/api/projects`);
    created.flush({
      ...projectFixture({ name: 'Q3 Board Deck' }),
      instructions: null,
    });
    await fixture.whenStable();

    const move = backend.expectOne(PUT_URL);
    expect(move.request.body.id).toBe(conversation.id);
    expect(move.request.body.projectId).toBeDefined();
    move.flush({ ...conversation, projectId: move.request.body.projectId });
  });

  it('states the ceiling when the drain stopped short', async () => {
    // Not in the design, but a list must never silently omit the project being looked for.
    const lookup = TestBed.inject(ProjectLookupStore);
    lookup.ensureLoaded();
    TestBed.tick();

    const page = projectPage(
      Array.from({ length: 100 }, () => projectFixture()),
      { totalCount: 4000 },
    );
    backend.expectOne((request) => request.url === PROJECTS_URL).flush(page);
    TestBed.tick();
    for (let skip = 100; skip < 500; skip += 100) {
      backend
        .expectOne((request) => request.url === PROJECTS_URL)
        .flush(
          projectPage(
            Array.from({ length: 100 }, () => projectFixture()),
            { totalCount: 4000, skip },
          ),
        );
      TestBed.tick();
    }

    fixture = TestBed.createComponent(PickerHost);
    await fixture.whenStable();
    fixture.componentInstance.anchorEl.set(fixture.componentInstance.anchor().nativeElement);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(getByText(host, /Showing the first 500 of 4000 projects\./)).toBeTruthy();
  });

  it('closes on Escape and returns focus to the anchor', async () => {
    const host = await render([projectFixture()]);

    panel(host)?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();

    expect(panel(host)).toBeNull();
    expect(document.activeElement).toBe(fixture.componentInstance.anchor().nativeElement);
  });

  it('refuses a move for a row whose own action is already in flight', async () => {
    const project = projectFixture();
    const conversation = conversationFixture({ projectId: null });
    const host = await render([project], conversation);

    // Put the row into the sidebar list and start a favourite on it, which is what
    // sets the pending id every one of these flows guards on.
    const list = TestBed.inject(ConversationListStore);
    list.prependNewest(conversation);
    TestBed.inject(ConversationActionsStore).toggleFavorite(conversation);
    const favorite = backend.expectOne(
      `${TEST_API_BASE_URL}/api/conversations/${conversation.id}/favorite`,
    );

    options(host)[0]?.click();
    await fixture.whenStable();

    backend.expectNone(PUT_URL);
    favorite.flush(null, { status: 204, statusText: 'No Content' });
  });
});
