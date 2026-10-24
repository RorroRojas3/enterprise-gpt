import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { getByRole } from '@testing-library/dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { tooltipOf } from '@testing/overlays';
import { Menu } from './menu';
import { MenuItem } from './menu-item';
import { MenuSeparator } from './menu-separator';
import { MenuTriggerContent } from './menu-trigger-content';

@Component({
  selector: 'app-menu-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Menu, MenuItem, MenuSeparator],
  template: `
    <app-menu
      label="Actions for Helios release"
      [hint]="hint()"
      [direction]="direction()"
      [(open)]="menuOpen"
    >
      <button id="rename" appMenuItem type="button">Rename</button>
      <button id="favourite" appMenuItem type="button">Favourite</button>
      <hr appMenuSeparator />
      <button id="delete" appMenuItem type="button" class="text-danger-emphasis">Delete</button>
    </app-menu>
    <div id="page">elsewhere</div>
  `,
})
class MenuHost {
  readonly menuOpen = signal(false);
  readonly hint = signal<string | null>(null);
  readonly direction = signal<'down' | 'up'>('down');
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
      trigger: getByRole(host, 'button', { name: 'Actions for Helios release' }),
      panel: () => host.querySelector('[role="menu"]'),
      async open() {
        getByRole(host, 'button', { name: 'Actions for Helios release' }).click();
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
    // An item pointerdown must not read as outside and remove the panel before the click;
    // `.click()` alone never exercises that.
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

  describe('the trigger hint', () => {
    /** On the trigger, not the role-less host, where axe would report `aria-prohibited-attr`. */
    it('renders a tooltip on the trigger when a hint is set', async () => {
      const menu = await render();
      menu.fixture.componentInstance.hint.set('Available when the response finishes');
      await menu.fixture.whenStable();

      menu.trigger.dispatchEvent(new MouseEvent('mouseenter'));
      await menu.fixture.whenStable();

      const flyout = tooltipOf(menu.trigger);
      expect(flyout?.textContent).toBe('Available when the response finishes');
    });

    /** A `down` trigger usually has nothing above it, where a `top` flyout would be clamped. */
    it('places the hint on the side the panel opens to', async () => {
      const menu = await render();
      menu.fixture.componentInstance.hint.set('Available when the response finishes');
      await menu.fixture.whenStable();

      menu.trigger.dispatchEvent(new MouseEvent('mouseenter'));
      await menu.fixture.whenStable();

      expect(tooltipOf(menu.trigger)?.getAttribute('data-placement')).toBe('bottom');
    });

    /** Pins the wiring, not the geometry; the re-measurement is covered in the directive. */
    it('follows a direction change made while the hint is showing', async () => {
      const menu = await render();
      menu.fixture.componentInstance.hint.set('Available when the response finishes');
      await menu.fixture.whenStable();
      menu.trigger.dispatchEvent(new MouseEvent('mouseenter'));
      await menu.fixture.whenStable();

      menu.fixture.componentInstance.direction.set('up');
      await menu.fixture.whenStable();

      expect(tooltipOf(menu.trigger)?.getAttribute('data-placement')).toBe('top');
    });

    /** Almost no menu passes a hint, so an absent one must render nothing and do no work. */
    it('renders nothing, and does no work at all, without one', async () => {
      const menu = await render();
      // Spied because the absent flyout alone would pass without the guard: the render-phase
      // check removes it either way.
      const addEventListener = vi.spyOn(document, 'addEventListener');

      menu.trigger.dispatchEvent(new MouseEvent('mouseenter'));
      await menu.fixture.whenStable();

      expect(tooltipOf(menu.trigger)).toBeNull();
      expect(addEventListener.mock.calls.filter(([type]) => type === 'keydown')).toHaveLength(0);

      addEventListener.mockRestore();
    });
  });

  /**
   * The one close path that bypasses `close()`; it destroys the panel holding focus, which would
   * otherwise land on `<body>` (WCAG 2.4.3).
   */
  it('returns focus to the trigger when a consumer closes it through the model', async () => {
    const menu = await render();
    await menu.open();

    expect(menu.panel()).not.toBeNull();
    expect(menu.host.contains(document.activeElement)).toBe(true);

    menu.fixture.componentInstance.menuOpen.set(false);
    await menu.fixture.whenStable();

    expect(menu.panel()).toBeNull();
    expect(document.activeElement).toBe(menu.trigger);
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
      trigger: getByRole(host, 'button', { name: 'Model: GPT-5 Enterprise' }),
      panel: () => host.querySelector('[role="menu"]'),
      async open() {
        getByRole(host, 'button', { name: 'Model: GPT-5 Enterprise' }).click();
        await fixture.whenStable();
      },
    };
  }

  it('renders the projected content inside the trigger and names it with the label', async () => {
    const { trigger } = await render();

    expect(trigger.querySelector('#pill')).not.toBeNull();
    expect(trigger.getAttribute('aria-label')).toBe('Model: GPT-5 Enterprise');
    // The icon button's square and fill drop away for a projected face.
    expect(trigger.classList).not.toContain('btn-icon');
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
    // The pickers swap content while open, and a top-anchored panel would grow down over the
    // trigger.
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

    // Simulates the retry flow dropping focus to <body>; the signal write supplies the re-render
    // that carries the fixup in zoneless.
    (document.activeElement as HTMLElement).blur();
    menu.component.firstChecked.set(true);
    await menu.fixture.whenStable();

    expect(document.activeElement?.id).toBe('first');
  });
});
