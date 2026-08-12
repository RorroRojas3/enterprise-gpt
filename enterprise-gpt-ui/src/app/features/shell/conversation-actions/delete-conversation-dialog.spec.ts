import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DeleteConversationDialog } from './delete-conversation-dialog';

describe('DeleteConversationDialog', () => {
  let fixture: ComponentFixture<DeleteConversationDialog>;
  let actions: InstanceType<typeof ConversationActionsStore>;
  let backend: HttpTestingController;
  let main: HTMLElement;

  const conversation = conversationFixture({ name: 'Vendor SSO escalation email' });

  beforeEach(() => {
    installDialogPolyfill();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    // The fallback focus target the shell normally provides.
    main = document.createElement('main');
    main.id = 'main-content';
    main.tabIndex = -1;
    document.body.appendChild(main);

    fixture = TestBed.createComponent(DeleteConversationDialog);
    actions = TestBed.inject(ConversationActionsStore);
    backend = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => {
    main.remove();
    backend.verify();
  });

  function dialog(): HTMLDialogElement {
    return fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
  }

  function deleteButton(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('.btn-danger') as HTMLButtonElement;
  }

  function cancelButton(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('.btn-outline-secondary') as HTMLButtonElement;
  }

  async function open(): Promise<void> {
    actions.beginDelete(conversation);
    await fixture.whenStable();
  }

  it('names the conversation and says what goes and what stays', async () => {
    await open();

    const body = fixture.nativeElement.querySelector('.delete__body');
    expect(body?.textContent).toContain('“Vendor SSO escalation email”');
    expect(body?.textContent).toContain('its messages will be permanently deleted');
    expect(body?.textContent).toContain('Files it uploaded stay in your documents');
  });

  it('offers a red Delete and lands initial focus on Cancel', async () => {
    await open();

    // Frame 3d: red lives only here and on bulk delete — and Enter on a freshly
    // opened destructive confirm must not destroy anything.
    expect(deleteButton().textContent).toContain('Delete');
    expect(cancelButton().hasAttribute('data-modal-autofocus')).toBe(true);
    expect(document.activeElement).toBe(cancelButton());
    expect(fixture.nativeElement.querySelectorAll('.btn-danger')).toHaveLength(1);
    // Direct projection into the footer — a wrapper div would collapse the gap.
    expect(deleteButton().parentElement?.classList.contains('modal-panel__footer')).toBe(true);
    expect(cancelButton().parentElement?.classList.contains('modal-panel__footer')).toBe(true);
  });

  it('confirms, closes at once, and moves fallen-through focus to the main landmark', async () => {
    await open();

    deleteButton().click();
    await fixture.whenStable();

    // Optimistic per the AC: the modal is gone before the server answers.
    expect(dialog().open).toBe(false);
    expect(actions.deleteTarget()).toBeNull();
    // The invoking row is gone with the delete, so focus cannot return to it; the
    // component moves a fall-through to #main-content rather than leaving <body>.
    expect(document.activeElement).toBe(main);

    backend
      .expectOne({ method: 'DELETE', url: `${TEST_API_BASE_URL}/api/conversations/${conversation.id}` })
      .flush(null, { status: 204, statusText: 'No Content' });
  });

  it('closes without a request through Cancel', async () => {
    await open();

    cancelButton().click();
    await fixture.whenStable();

    expect(actions.deleteTarget()).toBeNull();
    expect(dialog().open).toBe(false);
    backend.expectNone(() => true);
  });
});
