import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { onDismiss } from './dismiss';

function mount(html: string): HTMLElement {
  const host = document.createElement('div');
  host.innerHTML = html;
  document.body.append(host);
  return host;
}

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
