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
  form,
  hidden,
  maxLength,
  required,
  validate,
} from '@angular/forms/signals';
import { McpServerActionsStore } from '@core/catalog/mcp-server-actions-store';
import {
  AUTH_TYPE_OPTIONS,
  MCP_SERVER_FIELD_LENGTHS,
  McpServerFormValue,
  authTypeError,
  requiresScope,
  scopeError,
  toMcpServerFormValue,
  urlError,
} from '@core/catalog/mcp-server-form';
import { serverMessagesFor, unmatchedServerMessages } from '@core/errors/server-messages';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * The add-and-edit MCP server dialog, frame `5h` (US-1208).
 *
 * **One component, two modes**, as `ModelFormDialog` and `ProjectFormDialog` are — every
 * invoker talks to `McpServerActionsStore` and none of them talks to this dialog.
 *
 * It binds **five** fields where frame `5h` draws six. The missing one is the board's
 * **Linked permission** select, and it is absent because neither `CreateMcpServerActionDto`
 * nor `UpdateMcpServerActionDto` carries a permission field: `McpServerService` creates the
 * gating permission alongside the server, names it after the server and renames it in step.
 * There is nothing for such a control to bind, and rendering it disabled would be the
 * "shown and inert" affordance this codebase has refused twice already. The rule is stated
 * under the Name field instead, because it is *also* why a name can be refused for
 * colliding with a permission the administrator never created by hand.
 *
 * The board's three auth types are two — `None` and `EntraIdOnBehalfOf` — for the same
 * reason: those are the only two `McpAuthTypes` has.
 *
 * **Scope is conditional in both directions**, because the validator is: required for
 * Entra ID, and refused outright for None. The field is hidden and cleared on a switch to
 * None, so what the dialog shows is what the save will store — a value silently retained
 * behind a hidden field is the "blank versus zero" class of defect one screen over.
 */
@Component({
  selector: 'app-mcp-server-form-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormField, Modal],
  templateUrl: './mcp-server-form-dialog.html',
  styleUrl: './mcp-server-form-dialog.scss',
})
export class McpServerFormDialog {
  protected readonly actions = inject(McpServerActionsStore);
  protected readonly lengths = MCP_SERVER_FIELD_LENGTHS;
  protected readonly authTypes = AUTH_TYPE_OPTIONS;

  /** The form's backing model, reseeded from the target each time the dialog opens. */
  private readonly model = signal<McpServerFormValue>(toMcpServerFormValue(null));

  protected readonly f = form(this.model, (path) => {
    // A schema rule rather than a `[disabled]` binding: the `[formField]` directive owns
    // that attribute (NG8022), exactly as it owns `maxlength`. One rule at the root
    // covers every field.
    disabled(path, () => this.actions.formBusy());

    required(path.name, { message: 'A server name is required.' });
    maxLength(path.name, MCP_SERVER_FIELD_LENGTHS.name, {
      message: `Names are limited to ${MCP_SERVER_FIELD_LENGTHS.name} characters.`,
    });
    required(path.description, { message: 'A description is required.' });
    maxLength(path.description, MCP_SERVER_FIELD_LENGTHS.description, {
      message: `Descriptions are limited to ${MCP_SERVER_FIELD_LENGTHS.description} characters.`,
    });

    // The URL rule mirrors `BeAnAbsoluteHttpUri`, which is also the only place the
    // transport is constrained: `McpToolProvider` builds an HTTP transport
    // unconditionally, and stdio is neither supported nor modelled.
    maxLength(path.url, MCP_SERVER_FIELD_LENGTHS.url, {
      message: `URLs are limited to ${MCP_SERVER_FIELD_LENGTHS.url} characters.`,
    });
    validate(path.url, ({ value }) => clientError(urlError(value())));

    // The select offers only the two, but an *edit* can seed a third: the table renders
    // an auth type this build does not know rather than breaking the row, and opening
    // that row leaves the select with no matching option. Without this rule Save would be
    // enabled over a body `toMcpServerBody` refuses, and clicking it would do nothing.
    validate(path.authType, ({ value }) => clientError(authTypeError(value())));

    // `hidden` rather than only an `@if`: a hidden field contributes nothing to its
    // parent's validity, which is what stops the `None` arm from reaching a state where
    // Save is disabled by a rule whose field is not on screen to explain it. The `@if` in
    // the template is the guard the API itself recommends alongside it.
    hidden(path.scope, ({ valueOf }) => !requiresScope(Number(valueOf(path.authType))));
    maxLength(path.scope, MCP_SERVER_FIELD_LENGTHS.scope, {
      message: `Scopes are limited to ${MCP_SERVER_FIELD_LENGTHS.scope} characters.`,
    });
    // `valueOf` rather than the backing model: the rule depends on one sibling, and
    // reading the root signal would re-run it on every keystroke in Name, Description and
    // URL as well.
    validate(path.scope, ({ value, valueOf }) =>
      clientError(scopeError(value(), valueOf(path.authType))),
    );

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
  protected readonly heading = computed(() =>
    this.isEdit() ? 'Edit MCP server' : 'Add MCP server',
  );
  protected readonly submitLabel = computed(() => (this.isEdit() ? 'Save server' : 'Add server'));

  /**
   * Whether the Scope field applies at all — `None` refuses one rather than ignoring it.
   *
   * Derived from the field's own `hidden` state rather than re-deriving it from the auth
   * type, so the template and the schema cannot disagree about which fields are in play.
   */
  protected readonly showScope = computed(() => !this.f.scope().hidden());

  protected readonly canSubmit = computed(() => !this.f().invalid() && !this.actions.formBusy());

  /**
   * The seeded auth type is one this build does not offer.
   *
   * Reachable by editing a row the table renders through `authTypeLabel` but the select
   * cannot represent. It reaches the field ungated by `touched`, for the reason the
   * models dialog's provider 404 does: nobody typed it, so no amount of prior interaction
   * predicts it — and gating it would leave Save disabled with the explanation hidden
   * behind an interaction the disabled button prevents.
   */
  protected readonly unsupportedAuthType = computed(() => {
    const value = this.f.authType().value();

    return authTypeError(value) === null
      ? null
      : `This server uses an authentication type this version doesn’t support (${value}). Choose one of the options above to save it.`;
  });

  /**
   * Every field's messages and whether they are ready to show, in one computed.
   *
   * One object rather than ten members, and a computed rather than a method:
   * `provideCheckNoChangesConfig({ exhaustive: true })` compares bindings between passes,
   * and a method returning a fresh array would be a new value on every check.
   *
   * Field errors render once the reader has interacted with the field — or immediately
   * when they came from the server, which no amount of prior interaction predicts.
   */
  protected readonly fieldErrors = computed(() => {
    const fromServer = this.actions.formError() !== null;
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
   * Messages the five fields cannot carry — object-level rules, and any property the
   * server faults that no control here renders.
   *
   * A server message the reader cannot see is a save that silently fails.
   */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(this.actions.formError(), SERVER_FIELDS),
  );

  constructor() {
    // Both are tracked: the mode is the open, and the target is what decides whether the
    // fields are seeded from a server or from nothing.
    effect(() => {
      const mode = this.actions.formMode();
      const target = this.actions.formTarget();

      if (mode === null) {
        return;
      }

      untracked(() => {
        this.model.set(toMcpServerFormValue(target));
        // Clears touched/dirty left over from a previous open — the component is mounted
        // once and lives as long as the MCP tab.
        this.f().reset();
      });
    });

    // Switching to an auth type that refuses a scope clears the field rather than hiding
    // a value the save would discard. The write is untracked and guarded on the field
    // already being non-empty, so it converges in one extra pass rather than looping.
    effect(() => {
      const authType = this.f.authType().value();

      if (requiresScope(Number(authType))) {
        return;
      }

      untracked(() => {
        if (this.model().scope !== '') {
          this.model.update((value) => ({ ...value, scope: '' }));
        }
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

/** The five fields the server can key an `errors` entry to. */
const SERVER_FIELDS = ['name', 'description', 'url', 'authType', 'scope'] as const;

type FieldName = (typeof SERVER_FIELDS)[number];

/** One field's rendered state: whether to show messages, and which ones. */
interface FieldErrors {
  readonly show: boolean;
  readonly messages: readonly string[];
}

function clientError(message: string | null): { kind: string; message: string } | null {
  return message === null ? null : { kind: 'format', message };
}
