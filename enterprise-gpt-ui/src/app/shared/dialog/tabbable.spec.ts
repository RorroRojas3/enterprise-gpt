import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { onDismiss } from './dismiss';
import { captureFocusOrigin } from './focus-return';
import { tabbableWithin } from './tabbable';

function mount(html: string): HTMLElement {
  const host = document.createElement('div');
  host.innerHTML = html;
  document.body.append(host);
  return host;
}

describe('tabbableWithin', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('returns focusable elements in DOM order', () => {
    const host = mount(`
      <a href="#one">one</a>
      <button>two</button>
      <input />
    `);

    expect(tabbableWithin(host).map((element) => element.tagName)).toEqual([
      'A',
      'BUTTON',
      'INPUT',
    ]);
  });

  it('skips disabled, tabindex="-1", hidden, inert and aria-hidden subtrees', () => {
    const host = mount(`
      <button disabled>disabled</button>
      <button tabindex="-1">programmatic only</button>
      <button hidden>hidden</button>
      <div inert><button>inside inert</button></div>
      <div aria-hidden="true"><button>inside aria-hidden</button></div>
      <button id="only">real</button>
    `);

    expect(tabbableWithin(host).map((element) => element.id)).toEqual(['only']);
  });
});

describe('captureFocusOrigin', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('puts focus back where it was', () => {
    const host = mount('<button id="invoker">open</button><button id="other">other</button>');
    const invoker = host.querySelector<HTMLElement>('#invoker');
    invoker?.focus();

    const restore = captureFocusOrigin(document);
    host.querySelector<HTMLElement>('#other')?.focus();
    restore();

    expect(document.activeElement).toBe(invoker);
  });

  it('does nothing when the origin has been removed — a deleted row, say', () => {
    const host = mount('<button id="invoker">open</button>');
    host.querySelector<HTMLElement>('#invoker')?.focus();
    const restore = captureFocusOrigin(document);

    host.innerHTML = '';

    expect(() => restore()).not.toThrow();
  });
});

describe('onDismiss', () => {
  let controller: AbortController;

  beforeEach(() => {
    controller = new AbortController();
  });

  afterEach(() => {
    controller.abort();
    document.body.innerHTML = '';
  });

  it('reports Escape pressed inside the overlay', () => {
    const host = mount('<div id="overlay"></div>');
    const overlay = host.querySelector<HTMLElement>('#overlay') as HTMLElement;
    const onEscape = vi.fn();
    onDismiss(overlay, { onEscape }, controller.signal);

    overlay.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(onEscape).toHaveBeenCalledOnce();
  });

  it('reports a pointer that went down outside, and not one inside', () => {
    const host = mount(
      '<div id="overlay"><button id="inside">x</button></div><div id="page"></div>',
    );
    const overlay = host.querySelector<HTMLElement>('#overlay') as HTMLElement;
    const onOutside = vi.fn();
    onDismiss(overlay, { onOutside }, controller.signal);

    host
      .querySelector('#inside')
      ?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    expect(onOutside).not.toHaveBeenCalled();

    host.querySelector('#page')?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    expect(onOutside).toHaveBeenCalledOnce();
  });

  it('removes every listener when the signal aborts', () => {
    const host = mount('<div id="overlay"></div><div id="page"></div>');
    const overlay = host.querySelector<HTMLElement>('#overlay') as HTMLElement;
    const onEscape = vi.fn();
    const onOutside = vi.fn();
    onDismiss(overlay, { onEscape, onOutside }, controller.signal);

    controller.abort();

    overlay.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    host.querySelector('#page')?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));

    expect(onEscape).not.toHaveBeenCalled();
    expect(onOutside).not.toHaveBeenCalled();
  });
});
