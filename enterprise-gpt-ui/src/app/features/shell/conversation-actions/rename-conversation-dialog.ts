import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { FormField, form, maxLength, required, validate } from '@angular/forms/signals';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { serverMessagesFor, unmatchedServerMessages } from '@core/errors/server-messages';
import { Modal } from '@shared/overlay/modal/modal';

/** The server's `MaximumLength` on a conversation name; the counter and cap read it. */
const NAME_MAX_LENGTH = 256;

/**
 * The rename-conversation modal (US-304, frame `3c`).
 *
 * Mounted once in the shell and driven entirely by `ConversationActionsStore`, so the
 * sidebar row kebab and the conversation header kebab share one dialog without either
 * feature importing the other. Always a modal — never an inline input, never a
 * browser `prompt()`.
 *
 * The first Signal Form in the application, and the shape later ones copy: a
 * `signal` model, schema rules, and the server's verdict surfaced through a
 * declarative `validate` rule — it reports the store's `renameError` only while the
 * field still holds the exact name the server rejected, which is what makes the
 * message clear itself on the next edit without an imperative reset (Signal Forms
 * has no `setErrors`).
 */
@Component({
  selector: 'app-rename-conversation-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormField, Modal],
  templateUrl: './rename-conversation-dialog.html',
  styleUrl: './rename-conversation-dialog.scss',
})
export class RenameConversationDialog {
  protected readonly actions = inject(ConversationActionsStore);
  protected readonly nameMaxLength = NAME_MAX_LENGTH;

  /** The form's backing model, reseeded from the target each time the dialog opens. */
  private readonly model = signal({ name: '' });

  protected readonly f = form(this.model, (path) => {
    required(path.name, { message: 'A name is required.' });
    maxLength(path.name, NAME_MAX_LENGTH, {
      message: `Names are limited to ${NAME_MAX_LENGTH} characters.`,
    });
    validate(path.name, ({ value }) => {
      const rejected = this.actions.renameRejectedName();
      // Trimmed, because the store submits the trimmed value: the field may hold
      // surrounding whitespace the server never saw.
      if (rejected === null || value().trim() !== rejected) {
        return null;
      }

      return serverMessagesFor(this.actions.renameError(), 'name').map((message) => ({
        kind: 'server',
        message,
      }));
    });
  });

  protected readonly open = computed(() => this.actions.renameTarget() !== null);
  protected readonly charCount = computed(() => this.f.name().value().length);

  /**
   * Validity is consulted even though today's rules are all backstopped elsewhere
   * (the derived `maxlength`, the trim check): this is the form later stories copy,
   * and the first rule without a native backstop would otherwise submit invalid
   * values straight to the server. A side effect worth keeping: while the field
   * still holds the exact server-rejected name, the server rule makes it invalid,
   * so resubmitting the identical doomed value is blocked too.
   */
  protected readonly canSubmit = computed(
    () =>
      this.f.name().value().trim() !== '' &&
      !this.f.name().invalid() &&
      !this.actions.renameBusy(),
  );

  /**
   * Field errors render once the user has interacted — or immediately when they came
   * from the server, which no amount of prior interaction predicts.
   */
  protected readonly showErrors = computed(
    () =>
      this.f.name().invalid() &&
      (this.f.name().touched() || this.actions.renameError() !== null),
  );

  /**
   * The error region also exists when only unmatched (object-level) messages arrived,
   * and `aria-describedby` must point at it exactly when it renders.
   */
  protected readonly showErrorRegion = computed(
    () => this.showErrors() || this.unmatched().length > 0,
  );

  protected readonly errorMessages = computed(() =>
    this.f
      .name()
      .errors()
      .map((error) => error.message)
      .filter((message): message is string => message !== undefined),
  );

  /**
   * Messages under keys the form does not model — object-level rules and properties
   * this dialog never sends wrong (`Id`, `ProjectId`). Rendered rather than dropped,
   * because a server message the user cannot see is a rename that silently fails.
   */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(this.actions.renameError(), ['name']),
  );

  constructor() {
    effect(() => {
      const target = this.actions.renameTarget();
      if (target !== null) {
        untracked(() => {
          this.model.set({ name: target.name });
          // Clears touched/dirty left over from a previous open — the component is
          // mounted once and lives as long as the shell.
          this.f().reset();
        });
      }
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    // Enter submits without a blur, so touched would still be false and a
    // client-rule message would stay hidden exactly when it matters.
    this.f.name().markAsTouched();

    if (!this.canSubmit()) {
      return;
    }

    this.actions.submitRename(this.f.name().value());
  }
}
