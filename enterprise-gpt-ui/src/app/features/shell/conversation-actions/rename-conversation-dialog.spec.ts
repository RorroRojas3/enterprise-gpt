import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { ConversationDto } from '@domain/api/conversation';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { RenameConversationDialog } from './rename-conversation-dialog';

const PUT_URL = `${TEST_API_BASE_URL}/api/conversations`;

describe('RenameConversationDialog', () => {
  let fixture: ComponentFixture<RenameConversationDialog>;
  let actions: InstanceType<typeof ConversationActionsStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    installDialogPolyfill();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    fixture = TestBed.createComponent(RenameConversationDialog);
    actions = TestBed.inject(ConversationActionsStore);
    backend = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => {
    backend.verify();
  });

  function dialog(): HTMLDialogElement {
    return fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
  }

  function input(): HTMLInputElement {
    return fixture.nativeElement.querySelector('#rename-conversation-name') as HTMLInputElement;
  }

  function formElement(): HTMLFormElement {
    return fixture.nativeElement.querySelector('#rename-conversation-form') as HTMLFormElement;
  }

  function counter(): HTMLElement | null {
    return fixture.nativeElement.querySelector('#rename-conversation-counter');
  }

  function renameButton(): HTMLButtonElement {
    return fixture.nativeElement.querySelector(
      'button[form="rename-conversation-form"]',
    ) as HTMLButtonElement;
  }

  function cancelButton(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('.btn-outline-secondary') as HTMLButtonElement;
  }

  async function open(target: ConversationDto): Promise<void> {
    actions.beginRename(target);
    await fixture.whenStable();
  }

  async function type(value: string): Promise<void> {
    input().value = value;
    input().dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
  }

  async function submit(): Promise<void> {
    formElement().dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
  }

  it('opens pre-filled with the current name, focused, and capped at the server limit', async () => {
    await open(conversationFixture({ name: 'Helios 2.4 release status' }));

    expect(dialog().open).toBe(true);
    // Each button projects into the footer directly — a wrapper div would collapse
    // the flex row's gap and break frame 3c's button placement.
    expect(renameButton().parentElement?.classList.contains('modal-panel__footer')).toBe(true);
    expect(cancelButton().parentElement?.classList.contains('modal-panel__footer')).toBe(true);
    expect(input().value).toBe('Helios 2.4 release status');
    expect(input().getAttribute('maxlength')).toBe('256');
    expect(input().hasAttribute('data-modal-autofocus')).toBe(true);
    expect(document.activeElement).toBe(input());
  });

  it('counts characters live in the mono counter', async () => {
    // Frame 3c: `25 / 256` in JetBrains Mono, updating as the user types.
    await open(conversationFixture({ name: 'Helios 2.4 release status' }));
    expect(counter()?.textContent?.replace(/\s+/g, ' ').trim()).toBe('25 / 256');
    expect(counter()?.classList.contains('font-mono')).toBe(true);

    await type('New');
    expect(counter()?.textContent?.replace(/\s+/g, ' ').trim()).toBe('3 / 256');
  });

  it('disables Rename for a whitespace-only name', async () => {
    await open(conversationFixture());

    await type('   ');

    expect(renameButton().disabled).toBe(true);
  });

  it('submits the typed name and disables both buttons while the request is pending', async () => {
    const target = conversationFixture({ name: 'Before' });
    await open(target);

    await type('After');
    await submit();

    expect(renameButton().disabled).toBe(true);
    expect(cancelButton().disabled).toBe(true);
    expect(dialog().getAttribute('aria-busy')).toBe('true');

    backend.expectOne(PUT_URL).flush({ ...target, name: 'After' });
    await fixture.whenStable();

    expect(dialog().open).toBe(false);
  });

  it('renders the server rejection beneath the field and keeps the counter', async () => {
    // Frame 3c's right-hand variant: message left in the fail colour, counter still
    // right, input in its invalid state, nothing saved.
    const target = conversationFixture({ name: 'Before' });
    await open(target);

    await type('After');
    await submit();
    backend
      .expectOne(PUT_URL)
      .flush(PROBLEM_FIXTURES.validationError, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();

    expect(dialog().open).toBe(true);
    expect(input().classList.contains('is-invalid')).toBe(true);

    const errors = fixture.nativeElement.querySelector('#rename-conversation-errors');
    expect(errors?.textContent).toContain("'Name' must not be empty.");
    // A key the form does not model still renders — a message the user cannot see
    // is a rename that silently fails.
    expect(errors?.textContent).toContain("'Context Window Size' must be greater than 0.");
    expect(counter()).not.toBeNull();
  });

  it('clears the server message on the next edit', async () => {
    const target = conversationFixture({ name: 'Before' });
    await open(target);

    await type('After');
    await submit();
    backend
      .expectOne(PUT_URL)
      .flush(PROBLEM_FIXTURES.validationError, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();

    await type('After edited');

    expect(input().classList.contains('is-invalid')).toBe(false);
    expect(
      fixture.nativeElement.querySelector('#rename-conversation-errors')?.textContent ?? '',
    ).not.toContain("'Name' must not be empty.");
  });

  it('closes and resets through Cancel', async () => {
    await open(conversationFixture());

    cancelButton().click();
    await fixture.whenStable();

    expect(actions.renameTarget()).toBeNull();
    expect(dialog().open).toBe(false);
  });

  it('starts clean on a fresh open after a rejection', async () => {
    const target = conversationFixture({ name: 'Before' });
    await open(target);
    await type('After');
    await submit();
    backend
      .expectOne(PUT_URL)
      .flush(PROBLEM_FIXTURES.validationError, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();

    cancelButton().click();
    await fixture.whenStable();

    await open(conversationFixture({ name: 'Another conversation' }));

    expect(input().value).toBe('Another conversation');
    expect(input().classList.contains('is-invalid')).toBe(false);
    expect(fixture.nativeElement.querySelector('#rename-conversation-errors')).toBeNull();
  });
});
