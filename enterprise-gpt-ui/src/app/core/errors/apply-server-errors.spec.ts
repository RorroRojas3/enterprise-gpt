import { FormArray, FormControl, FormGroup, Validators } from '@angular/forms';
import { describe, expect, it } from 'vitest';
import { ValidationAppError } from './app-error';
import {
  SERVER_ERROR_KEY,
  applyServerErrors,
  clearServerErrors,
  serverErrorsOf,
} from './apply-server-errors';
import { ValidationProblemDetails } from './problem-details';
import { PROBLEM_TYPE } from './problem-types';

function validationError(errors: Record<string, string[]>): ValidationAppError {
  const problem: ValidationProblemDetails = {
    type: PROBLEM_TYPE.validationError,
    title: 'One or more validation errors occurred.',
    status: 400,
    errors,
  };

  return {
    kind: 'validation-error',
    status: 400,
    title: 'One or more validation errors occurred.',
    detail: null,
    traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
    instance: '/api/models',
    url: 'https://localhost:7045/api/models',
    message: 'One or more validation errors occurred.',
    problem,
    cause: null,
    errors,
  };
}

function modelForm() {
  return new FormGroup({
    name: new FormControl('', Validators.required),
    contextWindowSize: new FormControl(0),
    urlValue: new FormControl(''),
    id: new FormControl(''),
    chunking: new FormGroup({ maxTokens: new FormControl(0) }),
    files: new FormArray([new FormGroup({ fileName: new FormControl('') })]),
  });
}

describe('applyServerErrors — key normalization', () => {
  it.each([
    ['Name', 'name'],
    ['ContextWindowSize', 'contextWindowSize'],
    ['URLValue', 'urlValue'],
    ['ID', 'id'],
  ])('maps the PascalCase key %s onto the control %s', (key, controlName) => {
    const form = modelForm();

    const result = applyServerErrors(form, validationError({ [key]: ['bad'] }));

    expect(serverErrorsOf(form.controls[controlName as 'name'])).toEqual(['bad']);
    expect(result.unmatched).toEqual([]);
  });

  it('maps a dotted key onto a nested group', () => {
    const form = modelForm();

    applyServerErrors(form, validationError({ 'Chunking.MaxTokens': ['too large'] }));

    expect(serverErrorsOf(form.controls.chunking.controls.maxTokens)).toEqual(['too large']);
  });

  it('maps an indexed key onto a form array element', () => {
    const form = modelForm();

    applyServerErrors(form, validationError({ 'Files[0].FileName': ['required'] }));

    const element = form.controls.files.at(0) as FormGroup<{
      fileName: FormControl<string | null>;
    }>;
    expect(serverErrorsOf(element.controls.fileName)).toEqual(['required']);
  });

  it('strips a JSON pointer root from a binding-failure key', () => {
    const form = modelForm();

    applyServerErrors(form, validationError({ '$.Name': ['malformed'] }));

    expect(serverErrorsOf(form.controls.name)).toEqual(['malformed']);
  });

  it('falls back to the verbatim path for a control named PascalCase', () => {
    const form = new FormGroup({ Name: new FormControl('') });

    const result = applyServerErrors(form, validationError({ Name: ['bad'] }));

    expect(serverErrorsOf(form.controls.Name)).toEqual(['bad']);
    expect(result.unmatched).toEqual([]);
  });

  it('falls back to a case-insensitive match', () => {
    const form = new FormGroup({ deploymentname: new FormControl('') });

    applyServerErrors(form, validationError({ DeploymentName: ['bad'] }));

    expect(serverErrorsOf(form.controls.deploymentname)).toEqual(['bad']);
  });
});

describe('applyServerErrors — unmatched keys', () => {
  it('surfaces an object-level rule, which FluentValidation keys with an empty string', () => {
    const form = modelForm();

    const result = applyServerErrors(
      form,
      validationError({ '': ['At least one file is required.'] }),
    );

    expect(result.unmatched).toEqual([{ key: '', messages: ['At least one file is required.'] }]);
  });

  it('surfaces an unknown key without mutating any control or throwing', () => {
    const form = modelForm();

    const result = applyServerErrors(form, validationError({ Nope: ['bad'] }));

    expect(result.unmatched).toEqual([{ key: 'Nope', messages: ['bad'] }]);
    expect(serverErrorsOf(form.controls.name)).toEqual([]);
  });

  it('keeps the summary off the form root, where it would not survive', () => {
    // updateValueAndValidity walks up the tree and every ancestor replaces its whole
    // errors object with its own validators' output, so a root error set by hand is
    // gone after the first keystroke in any field — including an unrelated one.
    // Callers hold `unmatched` themselves instead.
    const form = modelForm();

    applyServerErrors(form, validationError({ '': ['Object-level failure.'] }));

    expect(serverErrorsOf(form)).toEqual([]);
  });

  it('reports the control paths it did apply', () => {
    const form = modelForm();

    const result = applyServerErrors(
      form,
      validationError({ Name: ['bad'], 'Chunking.MaxTokens': ['bad'] }),
    );

    expect([...result.applied].sort()).toEqual(['chunking.maxTokens', 'name']);
  });
});

describe('applyServerErrors — lifecycle', () => {
  it('marks matched controls as touched so the message renders immediately', () => {
    const form = modelForm();

    applyServerErrors(form, validationError({ Name: ['bad'] }));

    expect(form.controls.name.touched).toBe(true);
  });

  it('preserves client-side validation errors alongside the server ones', () => {
    const form = modelForm();

    applyServerErrors(form, validationError({ Name: ['bad'] }));

    expect(form.controls.name.errors?.['required']).toBe(true);
    expect(form.controls.name.errors?.[SERVER_ERROR_KEY]).toEqual(['bad']);
  });

  it('clears prior server errors on the next application', () => {
    const form = modelForm();
    applyServerErrors(form, validationError({ Name: ['first'] }));

    applyServerErrors(form, validationError({ ContextWindowSize: ['second'] }));

    expect(serverErrorsOf(form.controls.name)).toEqual([]);
    expect(serverErrorsOf(form.controls.contextWindowSize)).toEqual(['second']);
  });

  it('drops the server error as soon as the user edits the field', () => {
    // The dedicated error key is what buys this: Angular replaces `errors`
    // wholesale on the next validation pass, so the stale message cannot survive.
    const form = modelForm();
    applyServerErrors(form, validationError({ Name: ['bad'] }));

    form.controls.name.setValue('Anything');

    expect(serverErrorsOf(form.controls.name)).toEqual([]);
  });

  it('leaves client-side errors in place when cleared explicitly', () => {
    const form = modelForm();
    applyServerErrors(form, validationError({ Name: ['bad'] }));

    clearServerErrors(form);

    expect(serverErrorsOf(form.controls.name)).toEqual([]);
    expect(form.controls.name.errors?.['required']).toBe(true);
  });
});
