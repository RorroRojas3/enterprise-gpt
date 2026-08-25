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
  MCP_HEADER_LIMITS,
  MCP_SERVER_FIELD_LENGTHS,
  McpServerFormValue,
  authTypeError,
  headersError,
  requiresScope,
  scopeError,
  toMcpServerFormValue,
  urlError,
} from '@core/catalog/mcp-server-form';
import { serverMessagesFor, unmatchedServerMessages } from '@core/errors/server-messages';
import { BrandIcon } from '@shared/icon/brand-icon';
import { MCP_ICON_OPTIONS, mcpBrandIcon } from '@shared/icon/mcp-icon';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * The add-and-edit MCP server dialog, frame `5h` (US-1208).
 *
 * **One component, two modes**, as `ModelFormDialog` and `ProjectFormDialog` are — every
 * invoker talks to `McpServerActionsStore` and none of them talks to this dialog.
 *
 * It binds **seven** fields, five of them frame `5h`'s. The board's sixth is its
 * **Linked permission** select, and that one is absent because neither `CreateMcpServerActionDto`
 * nor `UpdateMcpServerActionDto` carries a permission field: `McpServerService` creates the
 * gating permission alongside the server, names it after the server and renames it in step.
 * There is nothing for such a control to bind, and rendering it disabled would be the
 * "shown and inert" affordance this codebase has refused twice already. The rule is stated
 * under the Name field instead, because it is *also* why a name can be refused for
 * colliding with a permission the administrator never created by hand. The last two fields
 * here are **Icon**, which the board predates (US-1210), and **Headers**, which carries the
 * per-server request headers some remote servers configure themselves through — the remote
 * Azure DevOps server's `X-MCP-Readonly` and `X-MCP-Toolsets` are why it exists.
 *
 * Headers are **not** the board's refused "Header key" auth type. That was an authentication
 * mechanism whose value is a bearer-equivalent secret, and it still does not exist: the Auth
 * type select still offers exactly two options. This field carries non-secret configuration,
 * the API refuses every header its transport owns, and the values render here in plain text
 * rather than being masked — which is the point, and the reason it is not a secret store.
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
  imports: [BrandIcon, FormField, Modal],
  templateUrl: './mcp-server-form-dialog.html',
  styleUrl: './mcp-server-form-dialog.scss',
})
export class McpServerFormDialog {
  protected readonly actions = inject(McpServerActionsStore);
  protected readonly lengths = MCP_SERVER_FIELD_LENGTHS;
  protected readonly authTypes = AUTH_TYPE_OPTIONS;
  protected readonly icons = MCP_ICON_OPTIONS;
  protected readonly headerLimits = MCP_HEADER_LIMITS;

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

    // Length only. The select cannot produce a malformed slug, and the API validates the
    // shape rather than the value on purpose — so a key written by a newer client has to
    // survive a round trip through this form rather than be refused by it.
    maxLength(path.iconKey, MCP_SERVER_FIELD_LENGTHS.iconKey, {
      message: `Icon keys are limited to ${MCP_SERVER_FIELD_LENGTHS.iconKey} characters.`,
    });

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

    // No `maxLength`: the field's budget is the serialized JSON the API stores, not the
    // characters typed, so the rule that owns it is the parser. It mirrors
    // `McpServerHeaderRules` and reports the first problem, naming the header at fault.
    validate(path.headers, ({ value }) => clientError(headersError(value())));

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

  /** The mark the Icon select's current value draws, or null for the generic glyph. */
  protected readonly iconPreview = computed(() => mcpBrandIcon(this.f.iconKey().value()));

  /**
   * The seeded icon key is one this build ships no artwork for.
   *
   * Same shape and same reason as {@link unsupportedAuthType}: reachable by editing a row
   * a newer client wrote, ungated by `touched` because nobody typed it. It does *not*
   * block Save — the key round-trips unchanged, so leaving it alone is the safe act and
   * the message says so.
   */
  protected readonly unsupportedIcon = computed(() => {
    const value = this.f.iconKey().value();

    return value === '' || this.icons.some((option) => option.value === value)
      ? null
      : `This server uses an icon this version doesn’t ship (${value}). It is kept as-is unless you choose another.`;
  });

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
   * Messages the seven fields cannot carry — object-level rules, and any property the
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

/**
 * The seven fields the server can key an `errors` entry to.
 *
 * `headers` is one key, not seven indexed ones: the API validates the whole set in a `Custom`
 * rule and faults it as `Headers`, which is what keeps this within `serverMessagesFor`'s
 * flat-keys-only contract.
 */
const SERVER_FIELDS = [
  'name',
  'description',
  'url',
  'authType',
  'scope',
  'iconKey',
  'headers',
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
