// jsdom implements neither `DragEvent` nor `DataTransfer` — there is no
// `DragEvent-impl.js` under its `living/events/` directory at all. Without these
// shims none of the US-204 specs can be written, so they ship with the story rather
// than as a convenience.
//
// `MouseEvent` is the right base: `DragEvent extends MouseEvent`, and `relatedTarget`
// — which the `dragleave` depth counter depends on — comes free from it. Only
// `dataTransfer` has to be grafted on.

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

/**
 * Dispatches a drag event and returns it, so a caller can assert on
 * `defaultPrevented` and on `dataTransfer.dropEffect`.
 *
 * `relatedTarget` is what the four-event child-`dragleave` sequence needs; the
 * browser sets it to `null` when the pointer leaves the window entirely.
 */
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
    // A real `types` is a frozen array in modern browsers and a DOMStringList in
    // older Safari; production code goes through Array.from for exactly that reason,
    // and an array satisfies both.
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
