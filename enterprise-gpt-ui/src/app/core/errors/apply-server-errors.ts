import { AbstractControl, FormArray, FormGroup } from '@angular/forms';
import { ValidationAppError } from './app-error';
import { toCamelCase } from './camel-case';

/** The error key server-side messages are stored under on a control. */
export const SERVER_ERROR_KEY = 'server';

/** An `errors` entry that matched no control. */
export interface UnmatchedServerError {
  /** The server's own property path, verbatim. */
  readonly key: string;
  readonly messages: readonly string[];
}

/** What {@link applyServerErrors} did. */
export interface ServerErrorResult {
  /** Dot-joined paths of the controls that received messages. */
  readonly applied: readonly string[];
  /**
   * Entries with no matching control — object-level rules, which FluentValidation
   * keys with the empty string, and any path the form does not model.
   *
   * Returned rather than written to the form root, because a root error set by
   * hand does not survive: `updateValueAndValidity` walks up the tree and every
   * ancestor replaces its whole `errors` object with its own validators' output,
   * so a summary attached to the root vanishes on the first keystroke in any
   * field. Hold this in a signal and render it from there.
   */
  readonly unmatched: readonly UnmatchedServerError[];
}

/**
 * Projects a validation problem's `errors` dictionary onto a reactive form.
 *
 * The server keys its dictionary by its own property paths, so they arrive
 * PascalCase (`ContextWindowSize`), dotted (`Chunking.MaxTokens`) and indexed
 * (`Files[0].FileName`) while controls are named after the camelCase JSON the
 * same DTO serializes to. Each key is therefore normalized with the server's own
 * camel-casing algorithm before lookup, with the verbatim path and a
 * case-insensitive walk as fallbacks.
 *
 * Messages land under a dedicated {@link SERVER_ERROR_KEY} so Angular drops them
 * on the control's next validation pass — the moment the user edits the field,
 * which is the behaviour they expect.
 *
 * Never throws, and never silently discards a key: anything that matched no
 * control comes back on {@link ServerErrorResult.unmatched} for the caller to
 * render as a summary.
 *
 * @param form The form or control group to apply messages to.
 * @param error The normalized validation error.
 */
export function applyServerErrors(
  form: AbstractControl,
  error: ValidationAppError,
): ServerErrorResult {
  clearServerErrors(form);

  const applied: string[] = [];
  const unmatched: UnmatchedServerError[] = [];

  for (const [key, messages] of Object.entries(error.errors)) {
    if (messages.length === 0) {
      continue;
    }

    const segments = parseKey(key);
    const match = segments.length > 0 ? resolveControl(form, segments) : null;

    if (match && match.control !== form) {
      setServerError(match.control, messages);
      applied.push(match.path.join('.'));
    } else {
      unmatched.push({ key, messages });
    }
  }

  return { applied, unmatched };
}

/**
 * Removes every {@link SERVER_ERROR_KEY} error from a control tree, leaving
 * client-side validation errors in place.
 *
 * @param control The root to clear.
 */
export function clearServerErrors(control: AbstractControl): void {
  const errors = control.errors;
  if (errors && SERVER_ERROR_KEY in errors) {
    const { [SERVER_ERROR_KEY]: _removed, ...rest } = errors;
    control.setErrors(Object.keys(rest).length > 0 ? rest : null);
  }

  for (const child of childrenOf(control)) {
    clearServerErrors(child);
  }
}

/**
 * Reads the server messages currently set on a control.
 *
 * @param control The control to inspect.
 */
export function serverErrorsOf(control: AbstractControl): readonly string[] {
  const value = control.errors?.[SERVER_ERROR_KEY];

  return Array.isArray(value) ? (value as string[]) : [];
}

function setServerError(control: AbstractControl, messages: readonly string[]): void {
  control.setErrors({ ...control.errors, [SERVER_ERROR_KEY]: [...messages] });
  control.markAsTouched();
}

/**
 * Splits a server property path into control-path segments, turning `[n]` groups
 * into their own numeric segments.
 */
function parseKey(key: string): (string | number)[] {
  // System.Text.Json binding failures prefix the path with a JSON pointer root.
  const normalized = key.replace(/^\$\.?/, '');
  if (normalized === '') {
    return [];
  }

  const segments: (string | number)[] = [];
  for (const part of normalized.split('.')) {
    const match = /^([^[]*)((?:\[\d+\])*)$/.exec(part);
    if (!match) {
      segments.push(part);
      continue;
    }

    const [, name = '', indices = ''] = match;
    if (name !== '') {
      segments.push(name);
    }

    for (const index of indices.matchAll(/\[(\d+)\]/g)) {
      segments.push(Number(index[1]));
    }
  }

  return segments;
}

/** A control that matched, together with the path it was found at. */
interface ControlMatch {
  readonly control: AbstractControl;
  readonly path: readonly (string | number)[];
}

function resolveControl(form: AbstractControl, segments: (string | number)[]): ControlMatch | null {
  // The array overload, never the dotted string: a numeric segment in a dotted
  // path is ambiguous between a FormArray index and a control literally named "0".
  const camel = segments.map((segment) =>
    typeof segment === 'number' ? segment : toCamelCase(segment),
  );

  const byCamelCase = form.get(camel);
  if (byCamelCase) {
    return { control: byCamelCase, path: camel };
  }

  const verbatim = form.get(segments);
  if (verbatim) {
    return { control: verbatim, path: segments };
  }

  return resolveCaseInsensitive(form, segments);
}

function resolveCaseInsensitive(
  form: AbstractControl,
  segments: (string | number)[],
): ControlMatch | null {
  let current: AbstractControl | null = form;
  const path: (string | number)[] = [];

  for (const segment of segments) {
    if (!current) {
      return null;
    }

    if (typeof segment === 'number') {
      current = current instanceof FormArray ? (current.at(segment) ?? null) : null;
      path.push(segment);
      continue;
    }

    if (!(current instanceof FormGroup)) {
      return null;
    }

    const name = Object.keys(current.controls).find(
      (candidate) => candidate.toLowerCase() === segment.toLowerCase(),
    );
    if (name === undefined) {
      return null;
    }

    current = current.get(name);
    path.push(name);
  }

  return current ? { control: current, path } : null;
}

function childrenOf(control: AbstractControl): AbstractControl[] {
  if (control instanceof FormGroup) {
    return Object.values(control.controls);
  }

  if (control instanceof FormArray) {
    return control.controls;
  }

  return [];
}
