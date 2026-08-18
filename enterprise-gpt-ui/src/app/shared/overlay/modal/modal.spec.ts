import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { beforeEach, describe, expect, it } from 'vitest';
import { Modal } from './modal';

@Component({
  selector: 'app-modal-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  template: `
    <button id="invoker" type="button" (click)="open.set(true)">Open</button>
    <app-modal
      [(open)]="open"
      heading="Rename conversation"
      [dismissible]="dismissible()"
      [busy]="busy()"
    >
      <input id="name" data-modal-autofocus />
      <div modalFooter><button id="cancel" type="button">Cancel</button></div>
    </app-modal>
  `,
})
class ModalHost {
  readonly open = signal(false);
  readonly dismissible = signal(true);
  readonly busy = signal(false);
}

describe('Modal', () => {
  beforeEach(() => {
    installDialogPolyfill();
    TestBed.configureTestingModule({});
  });

  async function render() {
    const fixture = TestBed.createComponent(ModalHost);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      component: fixture.componentInstance,
      dialog: host.querySelector('dialog') as HTMLDialogElement,
      invoker: host.querySelector('#invoker') as HTMLButtonElement,
    };
  }

  it('stays closed until asked', async () => {
    const { dialog } = await render();

    expect(dialog.open).toBe(false);
  });

  it('opens as a modal and labels itself from its own heading', async () => {
    const { fixture, component, dialog } = await render();

    component.open.set(true);
    await fixture.whenStable();

    expect(dialog.open).toBe(true);
    const labelledBy = dialog.getAttribute('aria-labelledby');
    expect(labelledBy).toBeTruthy();
    expect(dialog.querySelector(`#${labelledBy}`)?.textContent).toContain('Rename conversation');
  });

  it('moves focus to the autofocus target on open', async () => {
    const { fixture, component } = await render();

    component.open.set(true);
    await fixture.whenStable();

    expect((document.activeElement as HTMLElement).id).toBe('name');
  });

  it('returns focus to the invoking control on close', async () => {
    const { fixture, component, dialog, invoker } = await render();
    invoker.focus();

    component.open.set(true);
    await fixture.whenStable();
    dialog.close();
    await fixture.whenStable();

    expect(document.activeElement).toBe(invoker);
  });

  it('returns focus when the dialog is destroyed rather than closed (US-1401)', async () => {
    // A route change while a dialog is open destroys it, and `dialog.close()`
    // queues its event — so the `(close)` binding is torn down before the event
    // fires and focus would drop to `<body>`. The destroy path is what a user
    // hits by pressing browser Back with a dialog open, not an edge case.
    const { fixture, component, invoker } = await render();
    invoker.focus();
    component.open.set(true);
    await fixture.whenStable();
    expect(document.activeElement).not.toBe(invoker);

    fixture.destroy();

    expect(document.activeElement).toBe(invoker);
  });

  it('mirrors a native close back into its own state', async () => {
    const { fixture, component, dialog } = await render();
    component.open.set(true);
    await fixture.whenStable();

    dialog.close();
    await fixture.whenStable();

    expect(component.open()).toBe(false);
  });

  it('swallows Escape while a save is in flight', async () => {
    const { fixture, component, dialog } = await render();
    component.open.set(true);
    component.busy.set(true);
    await fixture.whenStable();

    const cancel = new Event('cancel', { cancelable: true });
    dialog.dispatchEvent(cancel);

    expect(cancel.defaultPrevented).toBe(true);
    expect(component.open()).toBe(true);
  });

  it('swallows Escape when it is not dismissible', async () => {
    const { fixture, component, dialog } = await render();
    component.open.set(true);
    component.dismissible.set(false);
    await fixture.whenStable();

    const cancel = new Event('cancel', { cancelable: true });
    dialog.dispatchEvent(cancel);

    expect(cancel.defaultPrevented).toBe(true);
  });

  it('closes on a backdrop click but not on a click inside the panel', async () => {
    const { fixture, component, dialog } = await render();
    component.open.set(true);
    await fixture.whenStable();

    dialog.querySelector('#cancel')?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await fixture.whenStable();
    expect(component.open()).toBe(true);

    dialog.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await fixture.whenStable();
    expect(component.open()).toBe(false);
  });

  it('does not close on a backdrop click while busy', async () => {
    const { fixture, component, dialog } = await render();
    component.open.set(true);
    component.busy.set(true);
    await fixture.whenStable();

    dialog.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await fixture.whenStable();

    expect(component.open()).toBe(true);
  });
});
