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
import { ModelActionsStore } from '@core/catalog/model-actions-store';
import {
  MODEL_FIELD_LENGTHS,
  ModelFormValue,
  PROVIDER_OPTIONS,
  priceError,
  toModelFormValue,
  tokenCountError,
} from '@core/catalog/model-form';
import { serverMessagesFor, unmatchedServerMessages } from '@core/errors/server-messages';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * The add-and-edit model dialog, frame `5f` (US-1207).
 *
 * **One component, two modes**, as `ProjectFormDialog` is — every invoker talks to
 * `ModelActionsStore` and none of them talks to this dialog.
 *
 * It binds **eleven** fields where frame `5f` draws eight. The three extra —
 * `isReasoningEnabled` and the two prices — are on `CreateModelActionDto` and
 * `UpdateModelActionDto`, and `ModelMapper` assigns all three unconditionally: an edit
 * that omitted a price would silently clear a figure a report is built on, and this is
 * the only surface in the app that can set one. Frame `5f`'s caption rules out an API
 * key, an endpoint URL and an "available to" selector, and none of those exists here —
 * that is the part of the caption this dialog is bound by.
 *
 * **No API key, no endpoint URL, no "available to".** Those do not exist in this system.
 *
 * The four numeric fields are bound as text rather than `type="number"`. The native
 * numeric control hands back `null` for anything it cannot parse, which loses what the
 * reader typed — and frame `5f` draws `400000.5` still sitting in its field beneath the
 * refusal. It is also the only way blank (unpriced) stays distinct from unparseable.
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
    // A schema rule rather than a `[disabled]` binding: the `[formField]` directive owns
    // that attribute (NG8022), exactly as it owns `maxlength`. One rule at the root
    // covers every field.
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

    // The whole-number rule is the client's own: the columns are `decimal(18,2)` and
    // their validator carries only `GreaterThan(0)`, so the server would accept
    // `400000.5`. Frame `5f` draws the refusal, so it has to live somewhere.
    validate(path.contextWindowSize, ({ value }) => clientError(tokenCountError(value())));
    validate(path.maxOutputTokens, ({ value }) => clientError(tokenCountError(value())));
    validate(path.inputPricePerMillionTokens, ({ value }) => clientError(priceError(value())));
    validate(path.outputPricePerMillionTokens, ({ value }) => clientError(priceError(value())));

    // The server's verdict, per field, reported only while the field still holds the
    // exact value that was sent — which is what clears it on the next keystroke, since
    // Signal Forms has no `setErrors` for an imperative reset to clear.
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
   * Every field's messages and whether they are ready to show, in one computed.
   *
   * One object rather than sixteen members, and a computed rather than a method:
   * `provideCheckNoChangesConfig({ exhaustive: true })` compares bindings between passes,
   * and a method returning a fresh array would be a new value on every check. The
   * computed's cached object is stable until a dependency actually changes.
   *
   * Field errors render once the reader has interacted with the field — or immediately
   * when they came from the server, which no amount of prior interaction predicts.
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
   * Messages the eight fields above cannot carry — object-level rules, and any property
   * the server faults that no control here *renders*. Rendered rather than dropped,
   * because a server message the reader cannot see is a save that silently fails.
   *
   * `SERVER_FIELDS`, not every key the form models: the three toggles are bound but have
   * no `validate` rule and no `fieldErrors()` entry, so passing them here would mark a
   * message about one as "matched" and then show it nowhere.
   */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(this.actions.formError(), SERVER_FIELDS),
  );

  /**
   * The provider 404, which reaches the Provider field by a different route than
   * `formError` does — it carries no `errors` dictionary at all.
   */
  protected readonly providerNotFound = computed(() => {
    const rejected = this.actions.formRejectedValue();
    const message = this.actions.formNotFound();

    return message !== null && rejected?.providerId === this.f.providerId().value()
      ? message
      : null;
  });

  constructor() {
    // Both are tracked: the mode is the open, and the target is what decides whether the
    // fields are seeded from a model or from nothing.
    effect(() => {
      const mode = this.actions.formMode();
      const target = this.actions.formTarget();

      if (mode === null) {
        return;
      }

      untracked(() => {
        this.model.set(toModelFormValue(target));
        // Clears touched/dirty left over from a previous open — the component is mounted
        // once and lives as long as the models tab.
        this.f().reset();
      });
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    // Enter submits without a blur, so `touched` would still be false and a client-rule
    // message would stay hidden exactly when it matters. Each field is marked in turn:
    // `markAsTouched` sets the status of the field it is called on, and `touched` on a
    // parent aggregates its descendants rather than propagating down to them.
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

/** One field's rendered state: whether to show messages, and which ones. */
interface FieldErrors {
  readonly show: boolean;
  readonly messages: readonly string[];
}

function clientError(message: string | null): { kind: string; message: string } | null {
  return message === null ? null : { kind: 'format', message };
}
