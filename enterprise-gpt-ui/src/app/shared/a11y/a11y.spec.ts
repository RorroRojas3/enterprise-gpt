import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { onDismiss } from './dismiss';
import { captureFocusOrigin } from './focus-return';
import { trapWithin } from './focus-trap';
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

describe('trapWithin', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  function tab(target: Element, shiftKey = false): KeyboardEvent {
    // cancelable matters: without it preventDefault() is a no-op and
    // defaultPrevented never becomes true, so the assertion would test nothing.
    const event = new KeyboardEvent('keydown', {
      key: 'Tab',
      shiftKey,
      bubbles: true,
      cancelable: true,
    });
    target.dispatchEvent(event);
    return event;
  }

  it('cycles forward from the last element to the first', () => {
    const host = mount('<button id="first">a</button><button id="last">b</button>');
    const release = trapWithin(host);
    host.querySelector<HTMLElement>('#last')?.focus();

    const event = tab(host);

    expect(event.defaultPrevented).toBe(true);
    expect((document.activeElement as HTMLElement).id).toBe('first');
    release();
  });

  it('cycles backward from the first element to the last', () => {
    const host = mount('<button id="first">a</button><button id="last">b</button>');
    const release = trapWithin(host);
    host.querySelector<HTMLElement>('#first')?.focus();

    tab(host, true);

    expect((document.activeElement as HTMLElement).id).toBe('last');
    release();
  });

  it('stops trapping once released', () => {
    const host = mount('<button id="first">a</button><button id="last">b</button>');
    const release = trapWithin(host);
    release();
    host.querySelector<HTMLElement>('#last')?.focus();

    expect(tab(host).defaultPrevented).toBe(false);
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
