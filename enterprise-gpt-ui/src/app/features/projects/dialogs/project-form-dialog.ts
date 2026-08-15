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
 * The create-and-edit project modal, frame `4h` (US-903).
 *
 * **One component, three entry points** — the board's own caption. The grid's New
 * project button and a project's header kebab drive it today; US-307's composer picker
 * adds the third without a change here, because every invoker talks to
 * `ProjectActionsStore` and none of them talks to this dialog.
 *
 * It copies `RenameConversationDialog`, the app's first Signal Form, including the part
 * that is easy to miss: the server's verdict arrives through a declarative `validate`
 * rule that reports it only while the field still holds the exact name the server
 * rejected. That is what clears the inline message on the next keystroke — Signal Forms
 * has no `setErrors` to clear, so an imperative reset has nothing to reset.
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
    // A schema rule rather than a `[disabled]` binding: the `[formField]` directive
    // owns that attribute (NG8022), exactly as it owns `maxlength`. One rule at the
    // root covers every field, which is what the edit dialog needs while the project
    // it is about is still being fetched.
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
      // Trimmed, because the store submits the trimmed value: the field may hold
      // surrounding whitespace the server never saw.
      if (rejected === null || value().trim() !== rejected) {
        return null;
      }

      // `projectNameMessages` rather than `serverMessagesFor`: the duplicate-name
      // message is the one string the board words for us, and this is where it is
      // swapped for the server's.
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

  /**
   * Field errors render once the user has interacted — or immediately when they came
   * from the server, which no amount of prior interaction predicts.
   */
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

  /**
   * Messages under keys the form does not model — object-level rules, and any property
   * this dialog never sends wrong. Rendered rather than dropped, because a server
   * message the user cannot see is a save that silently fails.
   */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(this.actions.formError(), ['name', 'description', 'instructions']),
  );

  protected readonly showNameRegion = computed(
    () => this.showNameErrors() || this.unmatched().length > 0,
  );

  constructor() {
    // Both are tracked. `formMode` is the open, and `formTarget` is what arrives one
    // round trip later when the grid's kebab had only the listing shape to open on —
    // seeding on the mode alone would leave those fields empty for good. Reseeding
    // cannot destroy typing: the fields are disabled for the whole of that fetch.
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
        // Clears touched/dirty left over from a previous open — the component is
        // mounted once and lives as long as the projects area.
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
