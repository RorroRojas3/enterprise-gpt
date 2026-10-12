import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { FormField, disabled, form, maxLength, required, validate } from '@angular/forms/signals';
import {
  MODEL_FIELD_LENGTHS,
  ModelFormValue,
  PROVIDER_OPTIONS,
  priceError,
  toModelFormValue,
  tokenCountError,
} from '@shared/utils/catalog/model-form';
import { serverMessagesFor, unmatchedServerMessages } from '@core/errors/server-messages';
import { ModelActionsStore } from '@state/model-actions-store';
import { Modal } from '@shared/components/modal/modal';

/**
 * Binds every field the PUT echoes, prices included, since an omitted one would be cleared.
 * Numeric fields are text inputs, so an unparseable entry stays visible and blank (unpriced)
 * stays distinct from invalid.
 */
@Component({
  selector: 'app-model-form-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormField, Modal],
  templateUrl: './model-form-dialog.html',
  styleUrl: './model-form-dialog.scss',
})
export class ModelFormDialog {
  protected readonly actions = inject(ModelActionsStore);
  protected readonly lengths = MODEL_FIELD_LENGTHS;
  protected readonly providers = PROVIDER_OPTIONS;

  /** The form's backing model, reseeded from the target each time the dialog opens. */
  private readonly model = signal<ModelFormValue>(toModelFormValue(null));

  protected readonly f = form(this.model, (path) => {
    // A schema rule, since `[formField]` owns the `disabled` attribute (NG8022).
    disabled(path, () => this.actions.formBusy());

    required(path.providerId, { message: 'Choose a provider.' });
    required(path.name, { message: 'A display name is required.' });
    maxLength(path.name, MODEL_FIELD_LENGTHS.name, {
      message: `Names are limited to ${MODEL_FIELD_LENGTHS.name} characters.`,
    });
    required(path.deploymentName, { message: 'A deployment name is required.' });
    maxLength(path.deploymentName, MODEL_FIELD_LENGTHS.deploymentName, {
      message: `Deployment names are limited to ${MODEL_FIELD_LENGTHS.deploymentName} characters.`,
    });
    required(path.description, { message: 'A description is required.' });
    maxLength(path.description, MODEL_FIELD_LENGTHS.description, {
      message: `Descriptions are limited to ${MODEL_FIELD_LENGTHS.description} characters.`,
    });

    // Whole numbers are a client-only rule: the server's `GreaterThan(0)` accepts `400000.5`.
    validate(path.contextWindowSize, ({ value }) => clientError(tokenCountError(value())));
    validate(path.maxOutputTokens, ({ value }) => clientError(tokenCountError(value())));
    validate(path.inputPricePerMillionTokens, ({ value }) => clientError(priceError(value())));
    validate(path.outputPricePerMillionTokens, ({ value }) => clientError(priceError(value())));

    // A hidden model cannot be the default: `GET api/models` would then resolve no default
    // and every composer would refuse to send.
    validate(path.isDefault, ({ value, valueOf }) =>
      value() && !valueOf(path.isUserSelectable)
        ? { kind: 'conflict', message: DEFAULT_CONFLICT_MESSAGE }
        : null,
    );

    // Shown only while the field holds the value that was sent, so the next keystroke
    // clears it; Signal Forms has no `setErrors`.
    for (const field of SERVER_FIELDS) {
      validate(path[field], ({ value }) => {
        const rejected = this.actions.formRejectedValue();
        if (rejected === null || value() !== rejected[field]) {
          return null;
        }

        return serverMessagesFor(this.actions.formError(), field).map((message) => ({
          kind: 'server',
          message,
        }));
      });
    }
  });

  protected readonly open = computed(() => this.actions.formMode() !== null);
  protected readonly isEdit = computed(() => this.actions.formMode() === 'edit');
  protected readonly heading = computed(() => (this.isEdit() ? 'Edit model' : 'Add model'));
  protected readonly submitLabel = computed(() => (this.isEdit() ? 'Save model' : 'Add model'));

  protected readonly canSubmit = computed(() => !this.f().invalid() && !this.actions.formBusy());

  /**
   * `isDefault` has no `fieldErrors()` entry, so this surfaces its rule. Shown at once, not on
   * touch: toggling the other switch can create the conflict.
   */
  protected readonly defaultConflict = computed(() => {
    const state = this.f.isDefault();

    return state.invalid()
      ? (state.errors().find((error) => error.kind === 'conflict')?.message ?? null)
      : null;
  });

  /**
   * A computed rather than a method, which would return a new array on every check. Server
   * errors show without prior interaction.
   */
  protected readonly fieldErrors = computed(() => {
    const fromServer = this.actions.formError() !== null || this.actions.formNotFound() !== null;
    const result = {} as Record<FieldName, FieldErrors>;

    for (const field of SERVER_FIELDS) {
      const state = this.f[field]();

      result[field] = {
        show: state.invalid() && (state.touched() || fromServer),
        messages: state
          .errors()
          .map((error) => error.message)
          .filter((message): message is string => message !== undefined),
      };
    }

    return result;
  });

  /**
   * Server messages no rendered field carries, shown so a save never fails silently. Keyed on
   * `SERVER_FIELDS`: the toggles have no `fieldErrors()` entry to show a match in.
   */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(this.actions.formError(), SERVER_FIELDS),
  );

  /** The provider 404 carries no `errors` dictionary, so it reaches the field this way. */
  protected readonly providerNotFound = computed(() => {
    const rejected = this.actions.formRejectedValue();
    const message = this.actions.formNotFound();

    return message !== null && rejected?.providerId === this.f.providerId().value()
      ? message
      : null;
  });

  constructor() {
    // Both tracked: the mode opens the dialog, and the target decides how it is seeded.
    effect(() => {
      const mode = this.actions.formMode();
      const target = this.actions.formTarget();

      if (mode === null) {
        return;
      }

      untracked(() => {
        this.model.set(toModelFormValue(target));
        // Mounted once for the whole tab, so clear touched/dirty from the previous open.
        this.f().reset();
      });
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    // Enter submits without a blur. Each field is marked in turn, since `touched` on a
    // parent only aggregates its descendants.
    for (const field of SERVER_FIELDS) {
      this.f[field]().markAsTouched();
    }

    if (!this.canSubmit()) {
      return;
    }

    this.actions.submitForm(this.model());
  }
}

/** The eight fields the server can key an `errors` entry to. */
const SERVER_FIELDS = [
  'providerId',
  'name',
  'deploymentName',
  'description',
  'contextWindowSize',
  'maxOutputTokens',
  'inputPricePerMillionTokens',
  'outputPricePerMillionTokens',
] as const;

type FieldName = (typeof SERVER_FIELDS)[number];

interface FieldErrors {
  readonly show: boolean;
  readonly messages: readonly string[];
}

/** Worded for the reader, and matching what both server validators say. */
const DEFAULT_CONFLICT_MESSAGE =
  'A model hidden from the model picker cannot be the default model.';

function clientError(message: string | null): { kind: string; message: string } | null {
  return message === null ? null : { kind: 'format', message };
}
