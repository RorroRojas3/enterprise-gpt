import {
  DOCUMENT,
  DestroyRef,
  EnvironmentProviders,
  inject,
  provideAppInitializer,
} from '@angular/core';

/**
 * Stops a file dropped outside a live drop target from replacing the application.
 *
 * The browser's default action for a dropped file is to navigate to it, so a user who
 * misses the composer by twenty pixels loses the page — and, mid-turn, the answer that
 * was streaming into it. There is no way to recover from that in script, so it is
 * prevented at the window instead.
 *
 * It lives in `core/dnd/` rather than under upload, deliberately: it has to work for a
 * user holding **no** `Upload File` grant at all, which is precisely the case US-204
 * describes. Filing it beside the upload feature would tie the protection to the
 * permission it exists to survive.
 *
 * Installed as an app initializer, matching `provideStartupShellHandoff()`, so
 * `app.config.ts` stays declarative and nothing has to inject a service purely for its
 * constructor side effect.
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
 * Bubble phase, never capture, and never `stopPropagation`.
 *
 * A live drop target is a descendant of the window, so in the bubble phase its handler
 * has already run with `dataTransfer.files` intact and has already called
 * `preventDefault()`. The second call here is a no-op on an event that was claimed, and
 * the only effective one on an event that was not. In the capture phase this ordering
 * inverts and every real drop target breaks.
 *
 * **Known constraint.** `defaultPrevented` detects an *author-installed* drop target
 * only. The browser's own handling of a drop onto `<input type="file">` never sets it,
 * so a file dragged onto a native file input would be marked `dropEffect = 'none'` here
 * and its drop cancelled. No such input exists today, and EP-8's attach control is a
 * click-to-browse button. A file input that also has to accept drops must either opt
 * out through a `dataset` flag checked here, or be wrapped in `FileDropTarget` so it
 * claims the event first.
 */
function onDragOver(event: DragEvent): void {
  if (!carriesFiles(event)) {
    return;
  }

  // `defaultPrevented` is how an unclaimed drag is told from a claimed one. Only the
  // unclaimed one gets the "no drop" cursor — overwriting a live target's `copy` here
  // would make a working zone look inert. `dropEffect` has to be set during `dragover`;
  // set on `dragenter` alone the browser ignores it.
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

/**
 * Text and link drags are left entirely alone.
 *
 * An unconditional `preventDefault()` here would swallow dragging selected text into
 * the composer's textarea — a legitimate gesture, and one the browser handles for free.
 * Only a file drag navigates, so only a file drag is suppressed.
 */
function carriesFiles(event: DragEvent): boolean {
  return Array.from(event.dataTransfer?.types ?? []).includes('Files');
}
