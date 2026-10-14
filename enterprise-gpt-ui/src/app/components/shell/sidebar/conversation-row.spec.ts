import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { getByRole, queryByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { ConversationActionsStore } from '@state/conversation-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { iconOf, menuItems, openMenu } from '@testing/overlays';
import { ConversationRow } from './conversation-row';

describe('ConversationRow', () => {
  let fixture: ComponentFixture<ConversationRow>;
  let actions: InstanceType<typeof ConversationActionsStore>;
  let backend: HttpTestingController;

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
    backend = TestBed.inject(HttpTestingController);
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  const KEBAB = 'Actions for Helios 2.4 release status';

  function kebab(): HTMLButtonElement {
    return getByRole(element(), 'button', { name: KEBAB });
  }

  async function openKebab(name = KEBAB): Promise<HTMLButtonElement[]> {
    return menuItems(await openMenu(fixture, name)) as HTMLButtonElement[];
  }

  it('keeps the kebab outside the navigation link', () => {
    const link = getByRole(element(), 'link', { name: /Helios 2.4 release status/ });

    expect(link.contains(kebab())).toBe(false);
    // Siblings in one row, so the row's hover and selection reach both.
    expect(link.parentElement?.contains(kebab())).toBe(true);
  });

  it('names the kebab after the conversation', () => {
    // `kebab()` finds it by exactly that name.
    expect(kebab().getAttribute('aria-haspopup')).toBe('menu');
  });

  it('routes Delete through the shared actions store, as the only red item', async () => {
    const items = await openKebab();

    const danger = items.filter((item) => item.classList.contains('text-danger-emphasis'));
    expect(danger).toHaveLength(1);
    expect(danger[0]?.textContent).toContain('Delete');
    expect(items.every((item) => item.querySelector('app-icon') !== null)).toBe(true);

    danger[0]?.click();
    await fixture.whenStable();

    expect(actions.deleteTarget()).toEqual(conversation);
  });

  it('marks its items aria-disabled while the row’s own action is in flight', async () => {
    TestBed.inject(ConversationListStore).setRowPending(conversation.id, true);
    const items = await openKebab();

    expect(items.length).toBeGreaterThan(0);
    // aria-disabled, not native: the items stay focusable for the roving focus.
    expect(items.every((item) => item.getAttribute('aria-disabled') === 'true')).toBe(true);
    expect(items.every((item) => !item.disabled)).toBe(true);
  });

  it('routes Rename through the shared actions store', async () => {
    const menu = await openMenu(fixture, KEBAB);

    getByRole(menu, 'menuitem', { name: 'Rename' }).click();
    await fixture.whenStable();

    expect(actions.renameTarget()).toEqual(conversation);
    // Activation closes the menu — the dialog is the next surface.
    expect(queryByRole(element(), 'menu')).toBeNull();
  });

  it('offers Favourite between Rename and Delete, and toggles it through the store', async () => {
    const items = await openKebab();

    // No Remove from project: this fixture is standalone.
    expect(items.map((item) => item.textContent?.trim())).toEqual([
      'Rename',
      'Favourite',
      'Move to project',
      'Delete',
    ]);
    expect(iconOf(items[1]!)).toBe('bi-star');

    items[1]?.click();
    await fixture.whenStable();

    const request = backend.expectOne(
      `${TEST_API_BASE_URL}/api/conversations/${conversation.id}/favorite`,
    );
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ isFavorite: true });
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('reads Unfavourite with a filled star on a row already favourited', async () => {
    const favourite = conversationFixture({ isFavorite: true });
    fixture.componentRef.setInput('conversation', favourite);
    await fixture.whenStable();

    // The row's own star is labelled, as it is the only mark of a favourite.
    expect(getByRole(element(), 'img', { name: 'Favourite' })).toBeTruthy();

    const menu = await openMenu(fixture, `Actions for ${favourite.name}`);
    const item = getByRole(menu, 'menuitem', { name: 'Unfavourite' });
    expect(iconOf(item)).toBe('bi-star-fill');
  });

  it('opens the picker from the kebab, anchored where the kebab was', async () => {
    const trigger = kebab();
    const menu = await openMenu(fixture, KEBAB);

    getByRole(menu, 'menuitem', { name: 'Move to project' }).click();
    await fixture.whenStable();

    expect(queryByRole(element(), 'menu')).toBeNull();
    const picker = getByRole(element(), 'dialog', {
      name: `Move ${conversation.name} to a project`,
    });

    // Escape returns focus to the control the panel was anchored to.
    picker.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();

    expect(queryByRole(element(), 'dialog')).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });
});
