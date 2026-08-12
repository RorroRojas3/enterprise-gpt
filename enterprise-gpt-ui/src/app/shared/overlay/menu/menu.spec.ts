import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Menu } from './menu';
import { MenuItem } from './menu-item';
import { MenuSeparator } from './menu-separator';
import { MenuTriggerContent } from './menu-trigger-content';

@Component({
  selector: 'app-menu-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Menu, MenuItem, MenuSeparator],
  template: `
    <app-menu label="Actions for Helios release" [(open)]="menuOpen">
      <button id="rename" appMenuItem type="button">Rename</button>
      <button id="favourite" appMenuItem type="button">Favourite</button>
      <hr appMenuSeparator />
      <button id="delete" appMenuItem type="button" class="menu__item--danger">Delete</button>
    </app-menu>
    <div id="page">elsewhere</div>
  `,
})
class MenuHost {
  readonly menuOpen = signal(false);
}

describe('Menu', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  async function render() {
    const fixture = TestBed.createComponent(MenuHost);
    // The panel positions itself from the trigger's box in an after-render effect, so
    // the fixture has to be attached to the document for that read to be meaningful.
    document.body.append(fixture.nativeElement as HTMLElement);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      host,
      trigger: host.querySelector('.menu__trigger') as HTMLButtonElement,
      panel: () => host.querySelector('[role="menu"]'),
      async open() {
        (host.querySelector('.menu__trigger') as HTMLButtonElement).click();
        await fixture.whenStable();
      },
      async keydown(key: string) {
        host
          .querySelector('[role="menu"]')
          ?.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
        await fixture.whenStable();
      },
    };
  }

  it('gives the trigger an accessible name and menu-button semantics', async () => {
    const { trigger } = await render();

    expect(trigger.getAttribute('aria-haspopup')).toBe('menu');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    expect(trigger.textContent).toContain('Actions for Helios release');
  });

  it('opens on click and points aria-controls at the panel', async () => {
    const menu = await render();

    await menu.open();

    expect(menu.panel()).not.toBeNull();
    expect(menu.trigger.getAttribute('aria-expanded')).toBe('true');
    expect(menu.trigger.getAttribute('aria-controls')).toBe(menu.panel()?.id);
  });

  it('gives every item the menuitem role and takes it out of the tab order', async () => {
    const menu = await render();
    await menu.open();

    const items = [...(menu.panel()?.querySelectorAll('[appMenuItem]') ?? [])];

    expect(items).toHaveLength(3);
    expect(items.every((item) => item.getAttribute('role') === 'menuitem')).toBe(true);
    expect(items.every((item) => item.getAttribute('tabindex') === '-1')).toBe(true);
  });

  it('focuses the first item on open and roves with the arrow keys', async () => {
    const menu = await render();

    await menu.open();
    expect((document.activeElement as HTMLElement).id).toBe('rename');

    await menu.keydown('ArrowDown');
    expect((document.activeElement as HTMLElement).id).toBe('favourite');

    await menu.keydown('End');
    expect((document.activeElement as HTMLElement).id).toBe('delete');

    await menu.keydown('ArrowDown');
    expect((document.activeElement as HTMLElement).id).toBe('rename');

    await menu.keydown('ArrowUp');
    expect((document.activeElement as HTMLElement).id).toBe('delete');

    await menu.keydown('Home');
    expect((document.activeElement as HTMLElement).id).toBe('rename');
  });

  it('opens from the keyboard onto the last item when ArrowUp is used', async () => {
    const menu = await render();

    menu.trigger.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true, cancelable: true }),
    );
    await menu.fixture.whenStable();

    expect((document.activeElement as HTMLElement).id).toBe('delete');
  });

  it('closes on Escape and returns focus to the trigger', async () => {
    const menu = await render();
    await menu.open();

    await menu.keydown('Escape');

    expect(menu.panel()).toBeNull();
    expect(document.activeElement).toBe(menu.trigger);
  });

  it('closes when an item is activated', async () => {
    const menu = await render();
    await menu.open();

    (menu.panel()?.querySelector('#rename') as HTMLButtonElement).click();
    await menu.fixture.whenStable();

    expect(menu.panel()).toBeNull();
  });

  it('survives the pointerdown that precedes a real mouse click on an item', async () => {
    // The regression this exists for: outside-dismiss was anchored to the trigger,
    // and the panel is the trigger's sibling — so a pointerdown on a menu item read
    // as "outside", the panel was removed before the click could be dispatched, and
    // every consumer's action silently did nothing. `.click()` alone never sees it.
    const menu = await render();
    await menu.open();
    const item = menu.panel()?.querySelector('#rename') as HTMLButtonElement;
    const activated = vi.fn();
    item.addEventListener('click', activated);

    item.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    await menu.fixture.whenStable();
    expect(menu.panel()).not.toBeNull();

    item.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await menu.fixture.whenStable();

    expect(activated).toHaveBeenCalledOnce();
    expect(menu.panel()).toBeNull();
  });

  it('registers dismissal when a parent opens it through the two-way binding', async () => {
    const menu = await render();

    // No trigger click: the state is set from outside, as `[(open)]` would.
    menu.fixture.componentInstance.menuOpen.set(true);
    await menu.fixture.whenStable();
    expect(menu.panel()).not.toBeNull();

    menu.host
      .querySelector('#page')
      ?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    await menu.fixture.whenStable();

    expect(menu.panel()).toBeNull();
  });

  it('closes on an outside pointer press without stealing focus', async () => {
    const menu = await render();
    await menu.open();

    menu.host
      .querySelector('#page')
      ?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    await menu.fixture.whenStable();

    expect(menu.panel()).toBeNull();
    expect(document.activeElement).not.toBe(menu.trigger);
  });

  it('closes on scroll rather than following the row', async () => {
    const menu = await render();
    await menu.open();

    document.dispatchEvent(new Event('scroll'));
    await menu.fixture.whenStable();

    expect(menu.panel()).toBeNull();
  });
});

@Component({
  selector: 'app-picker-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Menu, MenuItem, MenuTriggerContent],
  template: `
    <app-menu
      label="Model: GPT-5 Enterprise"
      [stayOpen]="stayOpen()"
      [disabled]="disabled()"
      [direction]="direction()"
      [panelWidth]="320"
      [(open)]="menuOpen"
    >
      <span appMenuTriggerContent id="pill">GPT-5 Enterprise</span>
      <button
        id="first"
        appMenuItem
        type="button"
        role="menuitemcheckbox"
        [attr.aria-checked]="firstChecked()"
        (click)="firstChecked.set(!firstChecked())"
      >
        Jira Cloud
      </button>
      <button
        id="second"
        appMenuItem
        type="button"
        role="menuitemradio"
        [attr.aria-checked]="false"
      >
        Confluence Search
      </button>
    </app-menu>
    <div id="page">elsewhere</div>
  `,
})
class PickerHost {
  readonly menuOpen = signal(false);
  readonly stayOpen = signal(true);
  readonly disabled = signal(false);
  readonly direction = signal<'down' | 'up'>('up');
  readonly firstChecked = signal(false);
}

describe('Menu with a projected trigger and checked items', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  async function render() {
    const fixture = TestBed.createComponent(PickerHost);
    document.body.append(fixture.nativeElement as HTMLElement);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      host,
      component: fixture.componentInstance,
      trigger: host.querySelector('.menu__trigger') as HTMLButtonElement,
      panel: () => host.querySelector('[role="menu"]'),
      async open() {
        (host.querySelector('.menu__trigger') as HTMLButtonElement).click();
        await fixture.whenStable();
      },
    };
  }

  it('renders the projected content inside the trigger and names it with the label', async () => {
    const { trigger } = await render();

    expect(trigger.querySelector('#pill')).not.toBeNull();
    expect(trigger.getAttribute('aria-label')).toBe('Model: GPT-5 Enterprise');
    expect(trigger.classList.contains('menu__trigger--content')).toBe(true);
    // The default icon face is replaced, not doubled.
    expect(trigger.querySelector('app-icon')).toBeNull();
  });

  it('carries the widened roles and aria-checked on the items', async () => {
    const menu = await render();
    await menu.open();

    const first = menu.panel()?.querySelector('#first');
    const second = menu.panel()?.querySelector('#second');

    expect(first?.getAttribute('role')).toBe('menuitemcheckbox');
    expect(first?.getAttribute('aria-checked')).toBe('false');
    expect(second?.getAttribute('role')).toBe('menuitemradio');
    expect(first?.getAttribute('tabindex')).toBe('-1');
  });

  it('keeps the panel open across item toggles when stayOpen is set', async () => {
    const menu = await render();
    await menu.open();

    const first = menu.panel()?.querySelector('#first') as HTMLButtonElement;
    first.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    first.click();
    await menu.fixture.whenStable();

    expect(menu.panel()).not.toBeNull();
    expect(menu.panel()?.querySelector('#first')?.getAttribute('aria-checked')).toBe('true');

    (menu.panel()?.querySelector('#first') as HTMLButtonElement).click();
    await menu.fixture.whenStable();

    expect(menu.panel()).not.toBeNull();
    expect(menu.panel()?.querySelector('#first')?.getAttribute('aria-checked')).toBe('false');
  });

  it('still dismisses a stayOpen panel on Escape and on an outside press', async () => {
    const menu = await render();
    await menu.open();

    menu
      .panel()
      ?.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
      );
    await menu.fixture.whenStable();
    expect(menu.panel()).toBeNull();
    expect(document.activeElement).toBe(menu.trigger);

    await menu.open();
    menu.host
      .querySelector('#page')
      ?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    await menu.fixture.whenStable();
    expect(menu.panel()).toBeNull();
  });

  it('keeps a disabled trigger focusable but inert', async () => {
    const menu = await render();
    menu.component.disabled.set(true);
    await menu.fixture.whenStable();

    expect(menu.trigger.getAttribute('aria-disabled')).toBe('true');
    expect(menu.trigger.hasAttribute('disabled')).toBe(false);

    menu.trigger.focus();
    expect(document.activeElement).toBe(menu.trigger);

    menu.trigger.click();
    await menu.fixture.whenStable();
    expect(menu.panel()).toBeNull();

    menu.trigger.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }),
    );
    await menu.fixture.whenStable();
    expect(menu.panel()).toBeNull();
  });

  it('anchors an upward panel by its bottom edge, so content growth extends up', async () => {
    // Top-anchoring was the bug: the pickers swap panel content while open
    // (retry → loading → rows), and a fixed top made the taller panel grow
    // *down* over the trigger and composer.
    const menu = await render();
    await menu.open();

    const panel = menu.panel() as HTMLElement;
    const triggerTop = menu.trigger.getBoundingClientRect().top;

    expect(panel.style.width).toBe('320px');
    expect(panel.style.top).toBe('');
    // window.innerHeight, not documentElement.clientHeight: fixed-position
    // `bottom` resolves against the viewport.
    expect(Number.parseFloat(panel.style.bottom)).toBe(window.innerHeight - triggerTop + 4);
  });

  it('closes on Escape from the trigger, where focus sits when no item took it', async () => {
    const menu = await render();
    await menu.open();

    menu.trigger.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    await menu.fixture.whenStable();

    expect(menu.panel()).toBeNull();
  });

  it('recovers focus into the panel when a swap drops it to the body', async () => {
    const menu = await render();
    await menu.open();
    expect(document.activeElement?.id).toBe('first');

    // Simulate the retry flow: the focused node goes away while the panel
    // stays open, dropping focus to <body> — always alongside a re-render,
    // which is what carries the fixup in zoneless.
    (document.activeElement as HTMLElement).blur();
    menu.component.firstChecked.set(true);
    await menu.fixture.whenStable();

    expect(document.activeElement?.id).toBe('first');
  });
});
