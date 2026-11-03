import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { recoverFocusOn } from './recover-focus';

@Component({
  selector: 'app-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (rows().includes('first')) {
      <button type="button" id="first">First</button>
    }
    @if (rows().includes('second')) {
      <button type="button" id="second">Second</button>
    }
  `,
})
class Rows {
  readonly rows = signal(['first', 'second']);

  constructor() {
    recoverFocusOn(this.rows);
  }
}

describe('recoverFocusOn', () => {
  let main: HTMLElement;
  let attached: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    main = document.createElement('main');
    main.id = 'main-content';
    main.tabIndex = -1;
    document.body.append(main);
  });

  afterEach(() => {
    main.remove();
    attached.remove();
  });

  async function render() {
    const fixture = TestBed.createComponent(Rows);
    await fixture.whenStable();
    attached = fixture.nativeElement as HTMLElement;
    document.body.append(attached);

    return { fixture, host: attached };
  }

  it('moves to the main content when the focused control leaves', async () => {
    const { fixture, host } = await render();
    host.querySelector<HTMLElement>('#first')!.focus();

    fixture.componentInstance.rows.set(['second']);
    await fixture.whenStable();

    expect(document.activeElement).toBe(main);
  });

  it('leaves focus on a control that stays', async () => {
    const { fixture, host } = await render();
    const second = host.querySelector<HTMLElement>('#second')!;
    second.focus();

    fixture.componentInstance.rows.set(['second']);
    await fixture.whenStable();

    expect(document.activeElement).toBe(second);
  });

  it('leaves focus that was outside the component', async () => {
    const { fixture } = await render();
    main.focus();
    const elsewhere = main.appendChild(document.createElement('button'));
    elsewhere.focus();

    fixture.componentInstance.rows.set([]);
    await fixture.whenStable();

    expect(document.activeElement).toBe(elsewhere);
  });
});
