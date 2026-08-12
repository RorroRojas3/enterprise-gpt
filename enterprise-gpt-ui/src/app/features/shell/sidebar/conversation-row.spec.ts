import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { beforeEach, describe, expect, it } from 'vitest';
import { ConversationRow } from './conversation-row';

describe('ConversationRow', () => {
  let fixture: ComponentFixture<ConversationRow>;
  let actions: InstanceType<typeof ConversationActionsStore>;

  const conversation = conversationFixture({ name: 'Helios 2.4 release status' });

  beforeEach(async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });

    fixture = TestBed.createComponent(ConversationRow);
    fixture.componentRef.setInput('conversation', conversation);
    actions = TestBed.inject(ConversationActionsStore);
    await fixture.whenStable();
  });

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('keeps the kebab outside the navigation link', () => {
    // An interactive element nested inside an <a> is invalid HTML and would fold the
    // menu into the link's accessible name.
    expect(element().querySelector('a app-menu')).toBeNull();
    expect(element().querySelector('.row app-menu')).not.toBeNull();
  });

  it('names the kebab after the conversation', () => {
    const trigger = element().querySelector<HTMLButtonElement>('.menu__trigger');

    expect(trigger?.textContent).toContain('Actions for Helios 2.4 release status');
  });

  it('routes Delete through the shared actions store, as the only red item', async () => {
    element().querySelector<HTMLButtonElement>('.menu__trigger')?.click();
    await fixture.whenStable();

    const items = [...element().querySelectorAll<HTMLButtonElement>('[appMenuItem]')];
    const danger = items.filter((item) => item.classList.contains('menu__item--danger'));
    expect(danger).toHaveLength(1);
    expect(danger[0]?.textContent).toContain('Delete');
    // Frame 3a: every item leads with its glyph, decorative beside the text.
    expect(items.every((item) => item.querySelector('app-icon') !== null)).toBe(true);

    danger[0]?.click();
    await fixture.whenStable();

    expect(actions.deleteTarget()).toEqual(conversation);
  });

  it('marks its items aria-disabled while the row’s own action is in flight', async () => {
    TestBed.inject(ConversationListStore).setRowPending(conversation.id, true);
    element().querySelector<HTMLButtonElement>('.menu__trigger')?.click();
    await fixture.whenStable();

    const items = [...element().querySelectorAll<HTMLButtonElement>('[appMenuItem]')];
    expect(items.length).toBeGreaterThan(0);
    // aria-disabled, not native: the items stay focusable for the roving focus.
    expect(items.every((item) => item.getAttribute('aria-disabled') === 'true')).toBe(true);
    expect(items.every((item) => !item.disabled)).toBe(true);
  });

  it('routes Rename through the shared actions store', async () => {
    element().querySelector<HTMLButtonElement>('.menu__trigger')?.click();
    await fixture.whenStable();

    const rename = [...element().querySelectorAll<HTMLButtonElement>('[appMenuItem]')].find(
      (item) => item.textContent?.includes('Rename'),
    );
    expect(rename).toBeDefined();

    rename?.click();
    await fixture.whenStable();

    expect(actions.renameTarget()).toEqual(conversation);
    // Activation closes the menu — the dialog is the next surface.
    expect(element().querySelector('.menu__panel')).toBeNull();
  });
});
