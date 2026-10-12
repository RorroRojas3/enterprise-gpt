import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { beforeEach, describe, expect, it } from 'vitest';
import { Offcanvas } from './offcanvas';

@Component({
  selector: 'app-offcanvas-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Offcanvas],
  template: `
    <button id="invoker" type="button" (click)="open.set(true)">Open</button>
    <app-offcanvas [(open)]="open" heading="User details" [side]="side()">
      <button id="inside" type="button">Edit</button>
      <div offcanvasFooter><button id="save" type="button">Save</button></div>
    </app-offcanvas>
  `,
})
class OffcanvasHost {
  readonly open = signal(false);
  readonly side = signal<'start' | 'end'>('end');
}

describe('Offcanvas', () => {
  beforeEach(() => {
    installDialogPolyfill();
    TestBed.configureTestingModule({});
  });

  async function render() {
    const fixture = TestBed.createComponent(OffcanvasHost);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      component: fixture.componentInstance,
      dialog: host.querySelector('dialog') as HTMLDialogElement,
      invoker: host.querySelector('#invoker') as HTMLButtonElement,
    };
  }

  it('opens as a modal dialog labelled by its heading', async () => {
    const { fixture, component, dialog } = await render();

    component.open.set(true);
    await fixture.whenStable();

    expect(dialog.open).toBe(true);
    const labelledBy = dialog.getAttribute('aria-labelledby');
    expect(dialog.querySelector(`#${labelledBy}`)?.textContent).toContain('User details');
  });

  it('moves focus inside on open and back to the invoker on close', async () => {
    const { fixture, component, dialog, invoker } = await render();
    invoker.focus();

    component.open.set(true);
    await fixture.whenStable();
    expect((document.activeElement as HTMLElement).id).toBe('inside');

    dialog.close();
    await fixture.whenStable();
    expect(document.activeElement).toBe(invoker);
  });

  it('projects a footer', async () => {
    const { fixture, component, dialog } = await render();
    component.open.set(true);
    await fixture.whenStable();

    expect(dialog.querySelector('.offcanvas-panel__footer #save')).not.toBeNull();
  });

  it('anchors to the opposite edge when side is start', async () => {
    const { fixture, component, dialog } = await render();
    component.side.set('start');
    await fixture.whenStable();

    expect(dialog.classList.contains('offcanvas-panel--start')).toBe(true);
  });
});
