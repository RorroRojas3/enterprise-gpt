import { afterEach, describe, expect, it } from 'vitest';
import { captureFocusOrigin } from './focus-return';

function mount(html: string): HTMLElement {
  const host = document.createElement('div');
  host.innerHTML = html;
  document.body.append(host);
  return host;
}

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
