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
import { PROJECT_FIELD_LENGTHS } from '@domain/api/project';
import { unmatchedServerMessages } from '@core/errors/server-messages';
import { projectNameMessages } from '@core/projects/duplicate-name';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * One dialog for every entry point, all of which go through `ProjectActionsStore`. Server
 * rejections surface via a `validate` rule keyed to the rejected name, as there is no `setErrors`.
 */
@Component({
  selector: 'app-project-form-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormField, Modal],
  templateUrl: './project-form-dialog.html',
  styleUrl: './project-form-dialog.scss',
})
export class ProjectFormDialog {
  protected readonly actions = inject(ProjectActionsStore);
  protected readonly lengths = PROJECT_FIELD_LENGTHS;

  /** The form's backing model, reseeded from the target each time the dialog opens. */
  private readonly model = signal({ name: '', description: '', instructions: '' });

  protected readonly f = form(this.model, (path) => {
    // A schema rule, not `[disabled]`, which `[formField]` owns (NG8022). At the root, so
    // every field stays disabled while an edit's project is still being fetched.
    disabled(path, () => this.actions.formLoading());
    required(path.name, { message: 'A name is required.' });
    maxLength(path.name, PROJECT_FIELD_LENGTHS.name, {
      message: `Names are limited to ${PROJECT_FIELD_LENGTHS.name} characters.`,
    });
    maxLength(path.description, PROJECT_FIELD_LENGTHS.description, {
      message: `Descriptions are limited to ${PROJECT_FIELD_LENGTHS.description} characters.`,
    });
    maxLength(path.instructions, PROJECT_FIELD_LENGTHS.instructions, {
      message: `Instructions are limited to ${PROJECT_FIELD_LENGTHS.instructions} characters.`,
    });
    validate(path.name, ({ value }) => {
      const rejected = this.actions.formRejectedName();
      // Trimmed, as the store submits it: the field may hold whitespace the server never saw.
      if (rejected === null || value().trim() !== rejected) {
        return null;
      }

      // Not `serverMessagesFor`: this swaps the server's duplicate-name sentence for ours.
      return projectNameMessages(this.actions.formError()).map((message) => ({
        kind: 'server',
        message,
      }));
    });
  });

  protected readonly open = computed(() => this.actions.formMode() !== null);
  protected readonly isEdit = computed(() => this.actions.formMode() === 'edit');
  protected readonly heading = computed(() => (this.isEdit() ? 'Edit project' : 'New project'));
  protected readonly submitLabel = computed(() => (this.isEdit() ? 'Save' : 'Create project'));

  protected readonly nameCount = computed(() => this.f.name().value().length);

  protected readonly canSubmit = computed(
    () =>
      this.f.name().value().trim() !== '' &&
      !this.f().invalid() &&
      !this.actions.formBusy() &&
      !this.actions.formLoading(),
  );

  /** After interaction, or at once for a server error, which no prior interaction predicts. */
  protected readonly showNameErrors = computed(
    () => this.f.name().invalid() && (this.f.name().touched() || this.actions.formError() !== null),
  );

  protected readonly nameErrors = computed(() => messagesOf(this.f.name().errors()));
  protected readonly descriptionErrors = computed(() => messagesOf(this.f.description().errors()));
  protected readonly instructionsErrors = computed(() =>
    messagesOf(this.f.instructions().errors()),
  );

  protected readonly showDescriptionErrors = computed(
    () => this.f.description().invalid() && this.f.description().touched(),
  );

  protected readonly showInstructionsErrors = computed(
    () => this.f.instructions().invalid() && this.f.instructions().touched(),
  );

  /** Messages under keys the form does not model, shown so none silently fails the save. */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(this.actions.formError(), ['name', 'description', 'instructions']),
  );

  protected readonly showNameRegion = computed(
    () => this.showNameErrors() || this.unmatched().length > 0,
  );

  constructor() {
    // Tracks `formTarget` too: an edit opened from a listing card gets its full project a
    // round trip later. Reseeding cannot destroy typing; the fields are disabled meanwhile.
    effect(() => {
      const mode = this.actions.formMode();
      const target = this.actions.formTarget();

      if (mode === null) {
        return;
      }

      untracked(() => {
        this.model.set({
          name: target?.name ?? '',
          description: target?.description ?? '',
          instructions: target?.instructions ?? '',
        });
        // Mounted once in `Shell`, so clear touched/dirty left by a previous open.
        this.f().reset();
      });
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    // Enter submits without a blur, so touched would still be false and a client-rule
    // message would stay hidden exactly when it matters.
    this.f.name().markAsTouched();

    if (!this.canSubmit()) {
      return;
    }

    this.actions.submitForm(this.model());
  }
}

function messagesOf(errors: readonly { readonly message?: string }[]): readonly string[] {
  return errors
    .map((error) => error.message)
    .filter((message): message is string => message !== undefined);
}
