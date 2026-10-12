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
import { serverMessagesFor, unmatchedServerMessages } from '@core/errors/server-messages';
import { ConversationActionsStore } from '@state/conversation-actions-store';
import { Modal } from '@shared/components/modal/modal';

/** The server's `MaximumLength` on a conversation name; the counter and cap read it. */
const NAME_MAX_LENGTH = 256;

/**
 * Mounted once in the shell, so both kebabs share it without importing each other. The server
 * rule reports `renameError` only while the field holds the rejected name, so it clears on the
 * next edit (Signal Forms has no `setErrors`).
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
      // Trimmed: the store submits the trimmed value, so the server never saw the whitespace.
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
   * Checks validity although today's rules have native backstops, so a rule without one cannot
   * submit; it also blocks resubmitting the exact name the server rejected.
   */
  protected readonly canSubmit = computed(
    () =>
      this.f.name().value().trim() !== '' && !this.f.name().invalid() && !this.actions.renameBusy(),
  );

  /** After interaction, or at once for a server error, which no interaction predicts. */
  protected readonly showErrors = computed(
    () =>
      this.f.name().invalid() && (this.f.name().touched() || this.actions.renameError() !== null),
  );

  /** Also rendered for unmatched messages alone; `aria-describedby` must follow it exactly. */
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
   * Keys the form does not model, rendered rather than dropped: a server message the user cannot
   * see is a rename that silently fails.
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
          // Clears touched/dirty from a previous open: the component lives as long as the shell.
          this.f().reset();
        });
      }
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    // Enter submits without a blur, which would leave a client-rule message hidden.
    this.f.name().markAsTouched();

    if (!this.canSubmit()) {
      return;
    }

    this.actions.submitRename(this.f.name().value());
  }
}
