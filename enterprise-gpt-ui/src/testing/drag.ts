// jsdom has neither `DragEvent` nor `DataTransfer`. These build on `MouseEvent`, which
// `DragEvent` extends, so `relatedTarget` comes free and only `dataTransfer` is grafted on.

/** What a drag is carrying. Files and text are the two cases the app distinguishes. */
export interface DragPayload {
  readonly files?: readonly File[];
  /** Defaults to `['Files']` when files are supplied, `['text/plain']` otherwise. */
  readonly types?: readonly string[];
}

/** Every drag event type the directive and the window suppressor listen for. */
export type DragEventType = 'dragenter' | 'dragover' | 'dragleave' | 'drop';

/** A drag carrying files, as an OS file drag reports it. */
export function fileDrag(...files: readonly File[]): DragPayload {
  return { files, types: ['Files'] };
}

/** A drag carrying selected text — the gesture that must keep working over a textarea. */
export function textDrag(): DragPayload {
  return { types: ['text/plain'] };
}

/** A `File` with no real bytes; nothing under test reads the content. */
export function textFile(name: string, contents = 'x'): File {
  return new File([contents], name, { type: 'text/plain' });
}

/** Dispatches a drag event and returns it, for asserting `defaultPrevented` and `dropEffect`. */
export function dispatchDrag(
  target: EventTarget,
  type: DragEventType,
  payload: DragPayload = fileDrag(),
  options: { relatedTarget?: EventTarget | null } = {},
): DragEvent {
  const event = new MouseEvent(type, {
    bubbles: true,
    cancelable: true,
    composed: true,
    relatedTarget: (options.relatedTarget ?? null) as EventTarget | null,
  });

  // defineProperty rather than assignment: MouseEvent has no dataTransfer slot, and
  // the handlers under test read it through the DragEvent type.
  Object.defineProperty(event, 'dataTransfer', {
    value: fakeDataTransfer(payload),
    configurable: true,
  });

  target.dispatchEvent(event);

  return event as DragEvent;
}

/**
 * The subset of `DataTransfer` the app touches: `types`, `files`, and a writable
 * `dropEffect` so a spec can prove the "no drop" cursor outside a live target.
 */
function fakeDataTransfer({ files = [], types }: DragPayload): DataTransfer {
  const resolvedTypes = types ?? (files.length > 0 ? ['Files'] : ['text/plain']);

  return {
    // Production reads `types` through Array.from for older Safari, so an array suffices.
    types: resolvedTypes,
    files: fakeFileList(files),
    dropEffect: 'none',
    effectAllowed: 'all',
  } as unknown as DataTransfer;
}

function fakeFileList(files: readonly File[]): FileList {
  const list: Record<number, File> = {};
  files.forEach((file, index) => (list[index] = file));

  return {
    ...list,
    length: files.length,
    item: (index: number) => files[index] ?? null,
    [Symbol.iterator]: () => files[Symbol.iterator](),
  } as unknown as FileList;
}
