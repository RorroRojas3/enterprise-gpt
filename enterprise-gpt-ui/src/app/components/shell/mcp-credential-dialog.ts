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
import { McpDto } from '@shared/models/api/mcp.model';
import { traceLine, userMessage } from '@core/errors/error-message';
import { serverMessagesFor } from '@core/errors/server-messages';
import { McpCredentialStore } from '@state/mcp-credential-store';
import { Modal } from '@shared/components/modal/modal';
import { apiKeyError } from './mcp-credential-form';

/**
 * Mounted once in the shell: the composer that opens it renders on both the chat and
 * project-detail routes, and a top-layer `<dialog>` does not care which is on screen.
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
   * Emptied whenever the dialog opens on a server, never seeded from the stored key: a masked
   * placeholder submitted unchanged would send the mask.
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
    // All of `McpCredentialRules` lives in `apiKeyError`, rather than half of it in the schema.
    validate(path.apiKey, ({ value }) => {
      const message = apiKeyError(value());

      return message === null ? null : [{ kind: 'shape', message }];
    });
    // Only while the field holds the exact value sent, so it clears on the next keystroke;
    // Signal Forms has no `setErrors`.
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
   * Only `busy` disables Save: a press is what reveals messages hidden behind `touched`, so a
   * disabled button would be a dead end.
   */
  protected readonly submitDisabled = computed(() => this.credentials.busy());

  /** For what the field cannot carry: a 403, a network drop, an unmodelled validation key. */
  protected readonly failure = computed(() => {
    const error = this.credentials.error();
    if (error === null) {
      return null;
    }

    // A field-level rejection already renders under the input; do not report it twice.
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

      // `linkedSignal` resets the value; this clears touched/dirty left by a previous open.
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
