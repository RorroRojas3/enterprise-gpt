import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { FormField, disabled, email, form, maxLength, required } from '@angular/forms/signals';
import { validate } from '@angular/forms/signals';
import { serverMessagesFor, unmatchedServerMessages } from '@core/errors/server-messages';
import { UserActionsStore } from '@core/users/user-actions-store';
import { Modal } from '@shared/overlay/modal/modal';
import { PermissionChecklist } from './permission-checklist';

/** Mirrors `CreateUserActionDtoValidator`, so a rejection is the exception. */
const EMAIL_MAX_LENGTH = 512;

/**
 * The add-user modal, frame `5b` (US-1202).
 *
 * **A work email and a permission checklist. Nothing else** — no name, role, status,
 * password or confirm-password field, because `CreateUserActionDto` has none of them:
 * the display name and the object id come from Microsoft Graph, and sign-in is Entra's.
 * The row's primary key *is* the directory object id, which is what lets the account the
 * administrator provisions here be the same row the person signs in against later.
 *
 * It copies `ProjectFormDialog`, the app's second Signal Form, including the part that is
 * easy to miss: the server's verdict arrives through a declarative `validate` rule that
 * reports it only while the field still holds the exact address the server rejected. That
 * is what clears the inline message on the next keystroke — Signal Forms has no
 * `setErrors`, so an imperative reset has nothing to reset.
 *
 * Four rejections reach that one field, and only three of them are validation problems.
 * The fourth — the directory has no user with this address — is a **404**, with no
 * `errors` dictionary to read, so it travels beside the validation error rather than
 * through it. A toast would leave the reader guessing which of the two things they filled
 * in was wrong.
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
    // A schema rule rather than a `[disabled]` binding: the `[formField]` directive owns
    // that attribute (NG8022), exactly as it owns `maxlength`.
    disabled(path, () => this.actions.formBusy());
    required(path.email, { message: 'A work email address is required.' });
    email(path.email, { message: 'Enter a valid email address.' });
    maxLength(path.email, EMAIL_MAX_LENGTH, {
      message: `Email addresses are limited to ${EMAIL_MAX_LENGTH} characters.`,
    });
    validate(path.email, ({ value }) => {
      const rejected = this.actions.formRejectedEmail();
      // Trimmed, because the store submits the trimmed value: the field may hold
      // surrounding whitespace the server never saw.
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

  /**
   * Field errors render once the user has interacted — or immediately when they came from
   * the server, which no amount of prior interaction predicts.
   */
  protected readonly showEmailErrors = computed(
    () =>
      this.f.email().invalid() &&
      (this.f.email().touched() ||
        this.actions.formError() !== null ||
        this.actions.formNotFound() !== null),
  );

  protected readonly emailErrors = computed(() => messagesOf(this.f.email().errors()));

  /**
   * Messages under keys the form does not model — object-level rules, and the per-element
   * `PermissionIds[n]` key the checklist cannot render under. Shown rather than dropped,
   * because a server message the user cannot see is a save that silently fails.
   */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(this.actions.formError(), ['email']),
  );

  protected readonly showErrorRegion = computed(
    () => this.showEmailErrors() || this.unmatched().length > 0,
  );

  /**
   * A field rather than a template arrow: `provideCheckNoChangesConfig({ exhaustive:
   * true })` compares bindings between passes, and a closure allocated in the template
   * is a new value on every check.
   */
  protected readonly retryPermissions = (): void => this.actions.retryPermissions();

  constructor() {
    effect(() => {
      const isOpen = this.open();
      if (!isOpen) {
        return;
      }

      untracked(() => {
        this.model.set({ email: '' });
        // Nothing is pre-checked. The server grants every `IsDefault` permission on top
        // of whatever is sent, so seeding the defaults here would render them as choices
        // an administrator could take away — which this request cannot do.
        this.selectedIds.set(new Set<string>());
        // Clears touched/dirty left over from a previous open — the component is mounted
        // once and lives as long as the administration area.
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
