import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DropOverlay } from '@shared/components/drop-overlay/drop-overlay';
import { dispatchDrag, fileDrag, textDrag, textFile } from '@testing/drag';
import { FileDropTarget } from './file-drop-target';

/** Stands in for the composer: an `@if`-gated attach control and a drop zone on one permission. */
@Component({
  selector: 'app-drop-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FileDropTarget, DropOverlay],
  template: `
    <div
      class="zone"
      [appFileDropTarget]="canUpload()"
      (filesDropped)="dropped($event)"
      #drop="appFileDropTarget"
    >
      <textarea class="child"></textarea>
      @if (drop.isDragOver()) {
        <app-drop-overlay />
      }
    </div>
    @if (canUpload()) {
      <button type="button" class="attach">Attach</button>
    }
  `,
})
class DropHost {
  readonly canUpload = signal(false);
  readonly dropped = vi.fn<(files: readonly File[]) => void>();
}

describe('FileDropTarget', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(canUpload: boolean) {
    const fixture = TestBed.createComponent(DropHost);
    fixture.componentInstance.canUpload.set(canUpload);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    const zone = host.querySelector<HTMLElement>('.zone');
    const child = host.querySelector<HTMLElement>('.child');

    if (!zone || !child) {
      throw new Error('The drop host did not render.');
    }

    return { fixture, host, zone, child };
  }

  const overlay = (host: HTMLElement): Element | null => host.querySelector('app-drop-overlay');

  it('offers nothing to a user without the Upload File grant', async () => {
    const { host, zone } = await render(false);

    expect(host.querySelector('.attach')).toBeNull();

    const over = dispatchDrag(zone, 'dragover');

    // Not prevented means the element never became a drop target, which is what
    // "drag-and-drop is inert" means to the browser.
    expect(over.defaultPrevented).toBe(false);
    expect(overlay(host)).toBeNull();
  });

  it('drops nothing for a user without the grant', async () => {
    const { fixture, zone } = await render(false);

    dispatchDrag(zone, 'drop', fileDrag(textFile('brief.txt')));

    expect(fixture.componentInstance.dropped).not.toHaveBeenCalled();
  });

  it('accepts a file for a user who holds the grant', async () => {
    const { fixture, host, zone } = await render(true);

    expect(host.querySelector('.attach')).not.toBeNull();

    const over = dispatchDrag(zone, 'dragenter');
    dispatchDrag(zone, 'dragover');
    await fixture.whenStable();

    expect(over.defaultPrevented).toBe(true);
    expect(overlay(host)).not.toBeNull();

    const file = textFile('brief.txt');
    dispatchDrag(zone, 'drop', fileDrag(file));
    await fixture.whenStable();

    expect(fixture.componentInstance.dropped).toHaveBeenCalledWith([file]);
    // `drop` consumes the drag outright; no balancing `dragleave` follows it.
    expect(overlay(host)).toBeNull();
  });

  it('stays lit while the pointer crosses into a child element', async () => {
    const { fixture, host, zone, child } = await render(true);

    dispatchDrag(zone, 'dragenter');
    dispatchDrag(child, 'dragenter', fileDrag(), { relatedTarget: zone });
    // The browser fires this as the pointer enters the child. A boolean flag clears
    // here and the overlay flickers off mid-drag; the depth counter does not.
    dispatchDrag(zone, 'dragleave', fileDrag(), { relatedTarget: child });
    await fixture.whenStable();

    expect(overlay(host)).not.toBeNull();

    dispatchDrag(child, 'dragleave', fileDrag(), { relatedTarget: null });
    await fixture.whenStable();

    expect(overlay(host)).toBeNull();
  });

  it('ignores a text drag, so dropping selected text into the composer still works', async () => {
    const { fixture, host, zone } = await render(true);

    dispatchDrag(zone, 'dragenter', textDrag());
    const over = dispatchDrag(zone, 'dragover', textDrag());
    await fixture.whenStable();

    expect(over.defaultPrevented).toBe(false);
    expect(overlay(host)).toBeNull();
  });

  it('clears and goes inert when the permission is revoked mid-drag', async () => {
    const { fixture, host, zone } = await render(true);

    dispatchDrag(zone, 'dragenter');
    await fixture.whenStable();
    expect(overlay(host)).not.toBeNull();

    // In production this is sign-out: SessionStore resets and canUploadFiles() flips.
    fixture.componentInstance.canUpload.set(false);
    await fixture.whenStable();

    expect(overlay(host)).toBeNull();
    expect(dispatchDrag(zone, 'dragover').defaultPrevented).toBe(false);

    dispatchDrag(zone, 'drop', fileDrag(textFile('brief.txt')));
    expect(fixture.componentInstance.dropped).not.toHaveBeenCalled();
  });

  // The directive only sees a boolean; an administrator without Upload File is covered by
  // `SessionStore.canUploadFiles` in `session-store.spec.ts`.
});
