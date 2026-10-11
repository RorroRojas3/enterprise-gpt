import {
  DOCUMENT,
  DestroyRef,
  EnvironmentProviders,
  inject,
  provideAppInitializer,
} from '@angular/core';

/**
 * Stops a file dropped outside a live drop target from navigating the browser away from the app.
 * Lives in `core/` rather than upload because it must protect users with no `Upload File` grant.
 */
export function providePreventDropNavigation(): EnvironmentProviders {
  return provideAppInitializer(() => {
    const window = inject(DOCUMENT).defaultView;

    if (!window) {
      return;
    }

    const controller = new AbortController();
    inject(DestroyRef).onDestroy(() => controller.abort());

    const options = { signal: controller.signal };
    window.addEventListener('dragover', onDragOver as EventListener, options);
    window.addEventListener('drop', onDrop as EventListener, options);
  });
}

/**
 * Bubble phase, never capture or `stopPropagation`, so a live drop target has already claimed the
 * event. A native `<input type="file">` never sets `defaultPrevented`, so one that must accept
 * drops needs an opt-out flag checked here or a `FileDropTarget` wrapper.
 */
function onDragOver(event: DragEvent): void {
  if (!carriesFiles(event)) {
    return;
  }

  // Only an unclaimed drag gets the "no drop" cursor: overwriting a live target's `copy` would
  // make it look inert. The browser ignores `dropEffect` set on `dragenter` alone.
  if (!event.defaultPrevented && event.dataTransfer) {
    event.dataTransfer.dropEffect = 'none';
  }

  event.preventDefault();
}

function onDrop(event: DragEvent): void {
  if (!carriesFiles(event)) {
    return;
  }

  event.preventDefault();
}

/** Only a file drag navigates; text drags into the composer must keep their default. */
function carriesFiles(event: DragEvent): boolean {
  return Array.from(event.dataTransfer?.types ?? []).includes('Files');
}
