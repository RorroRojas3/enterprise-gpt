import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import {
  FormField,
  disabled,
  email,
  form,
  maxLength,
  required,
  validate,
} from '@angular/forms/signals';
import { serverMessagesFor, unmatchedServerMessages } from '@core/errors/server-messages';
import { UserActionsStore } from '@state/user-actions-store';
import { Modal } from '@shared/components/modal/modal';
import { PermissionChecklist } from './permission-checklist';

/** Mirrors `CreateUserActionDtoValidator`, so a rejection is the exception. */
const EMAIL_MAX_LENGTH = 512;

/**
 * Server rejections surface through a `validate` rule keyed to the rejected address, as Signal
 * Forms has no `setErrors`. A directory 404 has no `errors` map, so it rides alongside.
 */
@Component({
  selector: 'app-user-form-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormField, Modal, PermissionChecklist],
  templateUrl: './user-form-dialog.html',
  styleUrl: './user-form-dialog.scss',
})
export class UserFormDialog {
  protected readonly actions = inject(UserActionsStore);

  /** The form's backing model, reseeded each time the modal opens. */
  private readonly model = signal({ email: '' });

  /** The checked permissions. Outside the form model: it is a set, not a field. */
  protected readonly selectedIds = signal<ReadonlySet<string>>(new Set<string>());

  protected readonly f = form(this.model, (path) => {
    // A schema rule, not a `[disabled]` binding: `[formField]` owns that attribute (NG8022).
    disabled(path, () => this.actions.formBusy());
    required(path.email, { message: 'A work email address is required.' });
    email(path.email, { message: 'Enter a valid email address.' });
    maxLength(path.email, EMAIL_MAX_LENGTH, {
      message: `Email addresses are limited to ${EMAIL_MAX_LENGTH} characters.`,
    });
    validate(path.email, ({ value }) => {
      const rejected = this.actions.formRejectedEmail();
      // Trimmed, as the store submits it: the field may hold whitespace the server never saw.
      if (rejected === null || value().trim() !== rejected) {
        return null;
      }

      const notFound = this.actions.formNotFound();

      return [
        ...serverMessagesFor(this.actions.formError(), 'email'),
        ...(notFound === null ? [] : [notFound]),
      ].map((message) => ({ kind: 'server', message }));
    });
  });

  protected readonly open = computed(() => this.actions.formMode() === 'create');

  protected readonly canSubmit = computed(
    () => this.f.email().value().trim() !== '' && !this.f().invalid() && !this.actions.formBusy(),
  );

  /** After interaction, or at once for a server error, which no prior interaction predicts. */
  protected readonly showEmailErrors = computed(
    () =>
      this.f.email().invalid() &&
      (this.f.email().touched() ||
        this.actions.formError() !== null ||
        this.actions.formNotFound() !== null),
  );

  protected readonly emailErrors = computed(() => messagesOf(this.f.email().errors()));

  /** Messages under keys the form does not model, shown so none silently fails the save. */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(this.actions.formError(), ['email']),
  );

  protected readonly showErrorRegion = computed(
    () => this.showEmailErrors() || this.unmatched().length > 0,
  );

  /** A field, not a template arrow: exhaustive no-changes checks see a new closure each pass. */
  protected readonly retryPermissions = (): void => this.actions.retryPermissions();

  constructor() {
    effect(() => {
      const isOpen = this.open();
      if (!isOpen) {
        return;
      }

      untracked(() => {
        this.model.set({ email: '' });
        // Nothing pre-checked: the server adds every `IsDefault` permission anyway, so seeded
        // defaults would look removable when they are not.
        this.selectedIds.set(new Set<string>());
        // Mounted once for the Users tab, so clear touched/dirty left by a previous open.
        this.f().reset();
      });
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    // Enter submits without a blur, so touched would still be false and a client-rule
    // message would stay hidden exactly when it matters.
    this.f.email().markAsTouched();

    if (!this.canSubmit()) {
      return;
    }

    this.actions.submitCreate(this.f.email().value().trim(), [...this.selectedIds()]);
  }
}

function messagesOf(errors: readonly { readonly message?: string }[]): readonly string[] {
  return errors
    .map((error) => error.message)
    .filter((message): message is string => message !== undefined);
}
