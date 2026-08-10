import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { Tooltip } from './tooltip';

@Component({
  selector: 'app-tooltip-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Tooltip],
  template: `
    <!-- The collapsed sidebar strip: an icon-only button whose glyph is decorative,
         so the control genuinely has no accessible name of its own. -->
    <button id="nameless" type="button" appTooltip="Conversations">
      <svg aria-hidden="true" focusable="false"></svg>
    </button>

    <button id="named" type="button" aria-label="Its own name" appTooltip="Extra detail">
      <svg aria-hidden="true" focusable="false"></svg>
    </button>

    <button id="labelled" type="button" appTooltip="Extra detail">Visible label</button>

    <input id="elsewhere" />
  `,
})
class TooltipHost {}

describe('Tooltip', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  async function render() {
    const fixture = TestBed.createComponent(TooltipHost);
    document.body.append(fixture.nativeElement as HTMLElement);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      nameless: host.querySelector('#nameless') as HTMLButtonElement,
      named: host.querySelector('#named') as HTMLButtonElement,
      labelled: host.querySelector('#labelled') as HTMLButtonElement,
      elsewhere: host.querySelector('#elsewhere') as HTMLInputElement,
    };
  }

  it('becomes the accessible name of a control that has none', async () => {
    const { nameless } = await render();

    expect(nameless.getAttribute('aria-label')).toBe('Conversations');
  });

  it('leaves an existing ARIA name alone', async () => {
    const { named } = await render();

    expect(named.getAttribute('aria-label')).toBe('Its own name');
  });

  it('leaves a visible text label alone, which SC 2.5.3 requires', async () => {
    const { labelled } = await render();

    expect(labelled.getAttribute('aria-label')).toBeNull();
  });

  it('shows on focus, not only on hover, and hides again on blur', async () => {
    const { fixture, nameless } = await render();

    nameless.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    await fixture.whenStable();
    expect(nameless.querySelector('.app-tooltip')?.textContent).toBe('Conversations');

    nameless.dispatchEvent(new FocusEvent('focusout', { bubbles: true }));
    await fixture.whenStable();
    expect(nameless.querySelector('.app-tooltip')).toBeNull();
  });

  it('renders the flyout as decoration, since the host already carries the text', async () => {
    const { fixture, nameless } = await render();

    nameless.dispatchEvent(new MouseEvent('mouseenter'));
    await fixture.whenStable();

    expect(nameless.querySelector('.app-tooltip')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('dismisses on Escape pressed anywhere, which is what SC 1.4.13 asks for', async () => {
    const { fixture, nameless, elsewhere } = await render();
    // Shown by hover, with focus somewhere else entirely — the case a host-scoped
    // Escape listener could never serve.
    nameless.dispatchEvent(new MouseEvent('mouseenter'));
    await fixture.whenStable();
    elsewhere.focus();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();

    expect(nameless.querySelector('.app-tooltip')).toBeNull();
  });
});
