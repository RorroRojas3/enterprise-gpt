import { ApplicationInitStatus, ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { providePreventDropNavigation } from '@core/dnd/prevent-drop-navigation';
import { dispatchDrag, fileDrag, textDrag, textFile } from '@testing/drag';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DropOverlay } from './drop-overlay';
import { FileDropTarget } from './file-drop-target';

/**
 * Stands in for the composer, which is US-401's to build. It carries the two halves of
 * US-204 that must move together: the `@if`-gated attach control and the drop zone,
 * both bound to the same permission value.
 */
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

    // Sign-out is the way this happens in production — US-205 resets SessionStore,
    // canUploadFiles() flips, and the zone must not be left lit up and listening.
    fixture.componentInstance.canUpload.set(false);
    await fixture.whenStable();

    expect(overlay(host)).toBeNull();
    expect(dispatchDrag(zone, 'dragover').defaultPrevented).toBe(false);

    dispatchDrag(zone, 'drop', fileDrag(textFile('brief.txt')));
    expect(fixture.componentInstance.dropped).not.toHaveBeenCalled();
  });

  // US-204's second criterion — an administrator without `Upload File` may not upload —
  // is not testable here and deliberately so: the directive takes a plain boolean and
  // has no access to permissions, which is what makes the check visible at every call
  // site. The property lives on `SessionStore.canUploadFiles` and is asserted in
  // `session-store.spec.ts`, against a real administrator whose grants omit Upload File.
});

describe('providePreventDropNavigation', () => {
  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [providePreventDropNavigation()] });
    // App initializers run when ApplicationInitStatus is first instantiated, not when
    // the testing module is configured.
    await TestBed.inject(ApplicationInitStatus).donePromise;
  });

  // Dispatched on document.body rather than through a live drop target: the directive
  // calls stopPropagation on a drop it has handled, so routing this through one would
  // make the suppressor look broken when it is doing exactly the right thing.
  it('prevents a file dropped outside any drop target from replacing the app', () => {
    const event = dispatchDrag(document.body, 'drop', fileDrag(textFile('brief.txt')));

    expect(event.defaultPrevented).toBe(true);
  });

  it('marks an unclaimed file drag as "no drop" rather than lying with a copy cursor', () => {
    const event = dispatchDrag(document.body, 'dragover');

    expect(event.defaultPrevented).toBe(true);
    expect(event.dataTransfer?.dropEffect).toBe('none');
  });

  it('leaves a text drag entirely alone', () => {
    const over = dispatchDrag(document.body, 'dragover', textDrag());
    const drop = dispatchDrag(document.body, 'drop', textDrag());

    expect(over.defaultPrevented).toBe(false);
    expect(drop.defaultPrevented).toBe(false);
  });
});
