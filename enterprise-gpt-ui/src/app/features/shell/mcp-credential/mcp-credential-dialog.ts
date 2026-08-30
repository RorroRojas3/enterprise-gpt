import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  linkedSignal,
  untracked,
} from '@angular/core';
import { FormField, disabled, form, required, validate } from '@angular/forms/signals';
import { apiKeyError } from '@core/catalog/mcp-credential-form';
import { McpCredentialStore } from '@core/catalog/mcp-credential-store';
import { McpDto } from '@domain/api/mcp';
import { traceLine, userMessage } from '@core/errors/error-message';
import { serverMessagesFor } from '@core/errors/server-messages';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * Where a user supplies the API key a tool server needs from them.
 *
 * Mounted once in the shell, beside the conversation and project dialogs, because the
 * composer that opens it renders on both the chat and project-detail routes and a native
 * `<dialog>` in the top layer does not care which of them is on screen.
 *
 * The field is `type="password"` with autofill off — the opposite of the MCP admin
 * dialog's header values, which render in plain text precisely because they are
 * configuration rather than secrets.
 */
@Component({
  selector: 'app-mcp-credential-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormField, Modal],
  templateUrl: './mcp-credential-dialog.html',
  styleUrl: './mcp-credential-dialog.scss',
})
export class McpCredentialDialog {
  protected readonly credentials = inject(McpCredentialStore);

  /**
   * The form's backing model, emptied whenever the dialog opens on a server.
   *
   * `linkedSignal` rather than an effect writing a plain signal: the value half of the reseed
   * is a computation of the target, and only the touched/dirty reset below needs an
   * imperative step. Never seeded from the stored key — there is none to seed from, and a
   * masked placeholder the user could submit unchanged would send the mask.
   */
  private readonly model = linkedSignal<McpDto | null, { apiKey: string }>({
    source: this.credentials.target,
    computation: () => ({ apiKey: '' }),
  });

  protected readonly f = form(this.model, (path) => {
    // A schema rule rather than a `[disabled]` binding: the `[formField]` directive owns
    // that attribute (NG8022).
    disabled(path, () => this.credentials.busy());
    required(path.apiKey, { message: 'Paste the token to continue.' });
    // Length, whitespace and character-set rules all live in `apiKeyError`, so one function
    // mirrors `McpCredentialRules` rather than the schema owning half of it.
    validate(path.apiKey, ({ value }) => {
      const message = apiKeyError(value());

      return message === null ? null : [{ kind: 'shape', message }];
    });
    // The server's verdict, reported only while the field still holds the exact value
    // that was sent — which is what clears it on the next keystroke, Signal Forms having
    // no `setErrors` for an imperative reset.
    validate(path.apiKey, ({ value }) => {
      const rejected = this.credentials.rejectedValue();
      if (rejected === null || value() !== rejected) {
        return null;
      }

      return serverMessagesFor(this.validationError(), 'apiKey').map((message) => ({
        kind: 'server',
        message,
      }));
    });
  });

  /** The rejection narrowed to the one arm `serverMessagesFor` can read. */
  private readonly validationError = computed(() => {
    const error = this.credentials.error();

    return error?.kind === 'validation-error' ? error : null;
  });

  protected readonly target = this.credentials.target;
  protected readonly open = computed(() => this.credentials.target() !== null);
  protected readonly hasKey = computed(() => this.credentials.target()?.hasUserApiKey === true);
  protected readonly heading = computed(() =>
    this.hasKey() ? 'Replace your API key' : 'Connect your account',
  );
  protected readonly submitLabel = computed(() => (this.hasKey() ? 'Replace key' : 'Save key'));

  protected readonly showErrors = computed(
    () =>
      this.f.apiKey().invalid() &&
      (this.f.apiKey().touched() || this.credentials.rejectedValue() !== null),
  );

  protected readonly errors = computed(() =>
    this.f
      .apiKey()
      .errors()
      .map((error) => error.message)
      .filter((message): message is string => message !== undefined),
  );

  protected readonly canSubmit = computed(() => !this.f().invalid() && !this.credentials.busy());

  /**
   * Only `busy` disables Save.
   *
   * A disabled button on a one-field dialog is a dead end: the reader presses it, nothing
   * happens, and the message explaining why is behind a `touched` flag that pressing Save
   * never sets. Letting the press through is what reveals it.
   */
  protected readonly submitDisabled = computed(() => this.credentials.busy());

  /**
   * The failure panel, for everything the field itself cannot carry — a 403, a network
   * drop, a validation key this form does not model.
   */
  protected readonly failure = computed(() => {
    const error = this.credentials.error();
    if (error === null) {
      return null;
    }

    // A field-level rejection already renders under the input; repeating it here would
    // report one refusal twice.
    if (serverMessagesFor(this.validationError(), 'apiKey').length > 0) {
      return null;
    }

    return { reason: userMessage(error), traceLine: traceLine(error) };
  });

  constructor() {
    effect(() => {
      const server = this.credentials.target();
      if (server === null) {
        return;
      }

      // The value is `linkedSignal`'s; this clears the touched/dirty flags a previous open
      // left behind, which the component being mounted once is what makes possible.
      untracked(() => this.f().reset());
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    // Enter submits without a blur, so touched would still be false and a client-rule
    // message would stay hidden exactly when it matters.
    this.f.apiKey().markAsTouched();

    if (!this.canSubmit()) {
      return;
    }

    this.credentials.save(this.model().apiKey);
  }
}
