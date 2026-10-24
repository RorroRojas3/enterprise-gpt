import {
  Directive,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
} from '@angular/core';

/**
 * Makes an element a file drop zone, or leaves it inert while it still renders. The permission
 * is a required input, not a `SessionStore` read, so the check stays visible at the call site.
 */
@Directive({
  selector: '[appFileDropTarget]',
  exportAs: 'appFileDropTarget',
})
export class FileDropTarget {
  /** Whether drops are accepted. Bind it to the permission, never to a role. */
  readonly appFileDropTarget = input.required<boolean>();

  /** Never emits an empty list — see the folder note on {@link onDrop}. */
  readonly filesDropped = output<readonly File[]>();

  private readonly _host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  /**
   * A depth, not a boolean: `dragleave` fires on entering a child, and Safari's null
   * `relatedTarget` rules out containment checks. Linked so revoking mid-drag clears it.
   */
  private readonly _depth = linkedSignal<boolean, number>({
    source: this.appFileDropTarget,
    computation: () => 0,
  });

  /** Whether a file drag is currently over the zone. Read it through `exportAs`. */
  readonly isDragOver = computed(() => this._depth() > 0);

  constructor() {
    effect((onCleanup) => {
      // Binding and unbinding only; `_depth` resets itself off the same source.
      if (!this.appFileDropTarget()) {
        return;
      }

      // Not `host:` listeners, which cannot be conditional: a disabled zone binds none at all.
      const controller = new AbortController();
      const options = { signal: controller.signal };

      this._host.addEventListener('dragenter', this.onDragEnter as EventListener, options);
      this._host.addEventListener('dragover', this.onDragOver as EventListener, options);
      this._host.addEventListener('dragleave', this.onDragLeave as EventListener, options);
      this._host.addEventListener('drop', this.onDrop as EventListener, options);

      onCleanup(() => controller.abort());
    });
  }

  private readonly onDragEnter = (event: DragEvent): void => {
    if (!carriesFiles(event)) {
      return;
    }

    event.preventDefault();
    this._depth.update((depth) => depth + 1);
  };

  private readonly onDragOver = (event: DragEvent): void => {
    if (!carriesFiles(event)) {
      return;
    }

    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = 'copy';
    }

    event.preventDefault();
  };

  private readonly onDragLeave = (event: DragEvent): void => {
    if (!carriesFiles(event)) {
      return;
    }

    // Clamped, because a `dragenter` that arrived while the directive was disabled
    // has no matching increment to undo.
    this._depth.update((depth) => Math.max(0, depth - 1));
  };

  private readonly onDrop = (event: DragEvent): void => {
    if (!carriesFiles(event)) {
      return;
    }

    event.preventDefault();
    // The innermost target wins; the window-level suppressor has nothing left to do.
    event.stopPropagation();
    // No `dragleave` follows a drop, and `dragend` never fires for an OS file drag.
    this._depth.set(0);

    const files = Array.from(event.dataTransfer?.files ?? []);

    // A dropped folder yields an empty `files` in several browsers (reading it needs
    // `webkitGetAsEntry()`); emit nothing rather than a silent `[]`.
    if (files.length > 0) {
      this.filesDropped.emit(files);
    }
  };
}

/**
 * Text and link drags are ignored so the composer's textarea keeps accepting dropped
 * text. Read through `Array.from` because `types` is a `DOMStringList` in older Safari.
 */
function carriesFiles(event: DragEvent): boolean {
  return Array.from(event.dataTransfer?.types ?? []).includes('Files');
}
