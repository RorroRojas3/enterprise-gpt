import { afterEach, describe, expect, it } from 'vitest';
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
