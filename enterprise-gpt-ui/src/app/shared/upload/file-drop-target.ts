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
 * Makes an element a file drop zone, or leaves it completely inert.
 *
 * US-204 asks for two different things and they need two different mechanisms. "No
 * attach control renders" is `@if` in the template — the same answer US-203 gave the
 * Admin nav entry, and it stays that way. "Drag-and-drop is inert" cannot be: the
 * prompt box still has to render, it just must not react. That is this directive.
 *
 * The permission is a **required input**, not an injected `SessionStore` read. Burying
 * an authorization decision inside a `shared/` primitive puts it where no reviewer
 * looks and makes the directive unusable for any non-upload drop; keeping it an input
 * makes the check visible at the call site, which is what "checked explicitly, never
 * inferred from `Administrator`" actually means. Required, so an unbound directive is
 * a `strictTemplates` compile error rather than a silently-open drop zone.
 *
 * @example
 * <div [appFileDropTarget]="session.canUploadFiles()"
 *      (filesDropped)="attach($event)"
 *      #drop="appFileDropTarget">
 *   <textarea …></textarea>
 *   &commat;if (drop.isDragOver()) { <app-drop-overlay /> }
 * </div>
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
   * Nesting depth, not a boolean.
   *
   * `dragleave` fires every time the pointer crosses into a *child*, so a boolean
   * clears the overlay while the pointer is still well inside the zone. The prettier
   * fix — checking whether `relatedTarget` is contained — is unreliable: Safari has
   * historically reported `relatedTarget: null` on those inner transitions, which is
   * indistinguishable from leaving the window.
   *
   * Linked to the permission rather than reset from inside the effect below: revoking
   * it mid-drag has to clear the overlay, and expressing that as a derivation makes it
   * a property of the type instead of an ordering the next editor has to preserve — and
   * settles it in one change-detection pass rather than two.
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

      // Raw addEventListener rather than `host:` metadata, because host bindings
      // cannot be attached conditionally and "binds no listeners at all when
      // disabled" is the criterion. Raw rather than Renderer2.listen because under
      // zoneless the signal write *is* the change-detection notification — there is
      // nothing for a zone-aware wrapper to do here.
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

    // `dropEffect` is only honoured when set during `dragover`, and `preventDefault`
    // on `dragover` is what makes the element a drop target at all — without it the
    // `drop` event never fires, which is the single most common reason a zone looks
    // right and does nothing.
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
    // The innermost target wins, and the window-level suppressor has nothing left to
    // do for an event that has already been claimed and handled.
    event.stopPropagation();
    // Hard reset: `drop` consumes the drag outright and no balancing `dragleave`
    // follows it. `dragend` is not an alternative — it never fires for an OS file
    // drag, because the drag did not start in this document.
    this._depth.set(0);

    const files = Array.from(event.dataTransfer?.files ?? []);

    // A dropped *folder* yields an empty `files` in several browsers; reading it needs
    // `webkitGetAsEntry()`, which belongs to EP-8. Emitting nothing is a visible
    // no-op the user can retry, where emitting `[]` would be a silent one.
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
