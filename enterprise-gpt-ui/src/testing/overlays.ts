import { ComponentFixture } from '@angular/core/testing';
import { getByRole } from '@testing-library/dom';

/**
 * Spec helpers for the overlay primitives. Specs find elements by role and name through
 * `@testing-library/dom`; these are the only places a spec names Bootstrap's overlay classes.
 */

/** Clicks the `app-menu` trigger named `name` under `root` and returns the menu it opens. */
export async function openMenu(
  fixture: ComponentFixture<unknown>,
  name: string | RegExp,
  root: HTMLElement = fixture.nativeElement as HTMLElement,
): Promise<HTMLElement> {
  getByRole(root, 'button', { name }).click();
  await fixture.whenStable();

  return getByRole(root, 'menu', { name });
}

/** The items of an open menu, checkbox and radio items included. */
export function menuItems(menu: HTMLElement): HTMLElement[] {
  return [...menu.querySelectorAll<HTMLElement>('[role^="menuitem"]')];
}

/** The flyout `appTooltip` renders inside `host`, or null while it is hidden. */
export function tooltipOf(host: Element): HTMLElement | null {
  return host.querySelector<HTMLElement>(':scope > .tooltip');
}

/** The open dialog named `name`, wherever it is rendered. */
export function dialogNamed(name: string | RegExp): HTMLElement {
  return getByRole(document.body, 'dialog', { name });
}

/** The sprite symbol an `<app-icon>` inside `element` draws, such as `bi-star-fill`. */
export function iconOf(element: Element): string | null {
  return element.querySelector('use')?.getAttribute('href')?.replace(/^#/, '') ?? null;
}
