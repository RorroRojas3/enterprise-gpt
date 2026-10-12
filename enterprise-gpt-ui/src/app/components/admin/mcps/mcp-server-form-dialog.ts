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
 * No permission control: the server creates and renames the linked permission with the server.
 * Headers are non-secret configuration, so they render in plain text. Scope is hidden and
 * cleared for `None`, so what the dialog shows is what the save stores.
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

  private readonly model = signal<McpServerFormValue>(toMcpServerFormValue(null));

  protected readonly f = form(this.model, (path) => {
    // A schema rule rather than `[disabled]`: `[formField]` owns that attribute (NG8022).
    disabled(path, () => this.actions.formBusy());

    required(path.name, { message: 'A server name is required.' });
    maxLength(path.name, MCP_SERVER_FIELD_LENGTHS.name, {
      message: `Names are limited to ${MCP_SERVER_FIELD_LENGTHS.name} characters.`,
    });
    required(path.description, { message: 'A description is required.' });
    maxLength(path.description, MCP_SERVER_FIELD_LENGTHS.description, {
      message: `Descriptions are limited to ${MCP_SERVER_FIELD_LENGTHS.description} characters.`,
    });

    // Mirrors `BeAnAbsoluteHttpUri`: `McpToolProvider` only builds HTTP transports.
    maxLength(path.url, MCP_SERVER_FIELD_LENGTHS.url, {
      message: `URLs are limited to ${MCP_SERVER_FIELD_LENGTHS.url} characters.`,
    });
    validate(path.url, ({ value }) => clientError(urlError(value())));

    // An edit can seed an auth type the select does not offer; without this rule Save would
    // be enabled over a body `toMcpServerBody` refuses.
    validate(path.authType, ({ value }) => clientError(authTypeError(value())));

    // Length only, so a key written by a newer client survives a round trip.
    maxLength(path.iconKey, MCP_SERVER_FIELD_LENGTHS.iconKey, {
      message: `Icon keys are limited to ${MCP_SERVER_FIELD_LENGTHS.iconKey} characters.`,
    });

    // `hidden` as well as the template's `@if`, so an off-screen Scope rule cannot keep Save
    // disabled under `None`.
    hidden(path.scope, ({ valueOf }) => !requiresScope(Number(valueOf(path.authType))));
    maxLength(path.scope, MCP_SERVER_FIELD_LENGTHS.scope, {
      message: `Scopes are limited to ${MCP_SERVER_FIELD_LENGTHS.scope} characters.`,
    });
    // `valueOf` rather than the model, so other fields' keystrokes do not re-run the rule.
    validate(path.scope, ({ value, valueOf }) =>
      clientError(scopeError(value(), valueOf(path.authType))),
    );

    // No `maxLength`: the budget is the serialized JSON, which the parser (mirroring
    // `McpServerHeaderRules`) checks.
    validate(path.headers, ({ value }) => clientError(headersError(value())));

    // Only while the field holds the rejected value, so the next keystroke clears it;
    // Signal Forms has no `setErrors`.
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

  /** Read from the field's `hidden` state, so the template and the schema cannot disagree. */
  protected readonly showScope = computed(() => !this.f.scope().hidden());

  protected readonly iconPreview = computed(() => mcpBrandIcon(this.f.iconKey().value()));

  /** Ungated by `touched`, as the value was seeded; it does not block Save, the key round-trips. */
  protected readonly unsupportedIcon = computed(() => {
    const value = this.f.iconKey().value();

    return value === '' || this.icons.some((option) => option.value === value)
      ? null
      : `This server uses an icon this version doesn’t ship (${value}). It is kept as-is unless you choose another.`;
  });

  protected readonly canSubmit = computed(() => !this.f().invalid() && !this.actions.formBusy());

  /** Ungated by `touched`: the value was seeded, and gating it would hide why Save is disabled. */
  protected readonly unsupportedAuthType = computed(() => {
    const value = this.f.authType().value();

    return authTypeError(value) === null
      ? null
      : `This server uses an authentication type this version doesn’t support (${value}). Choose one of the options above to save it.`;
  });

  /**
   * A computed, not a method, which exhaustive no-changes checks would see as a fresh array.
   * Server errors show without `touched`, since no interaction predicts them.
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

  /** Server messages no field here renders; hiding them would make the save fail silently. */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(this.actions.formError(), SERVER_FIELDS),
  );

  constructor() {
    // Both tracked: the mode opens the dialog, and the target decides what seeds the fields.
    effect(() => {
      const mode = this.actions.formMode();
      const target = this.actions.formTarget();

      if (mode === null) {
        return;
      }

      untracked(() => {
        this.model.set(toMcpServerFormValue(target));
        // The component outlives each open, so clear the previous open's touched/dirty.
        this.f().reset();
      });
    });

    // Clears rather than hides a scope the save would discard; the non-empty guard makes it
    // converge in one extra pass.
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
    // Enter submits without a blur. Each field is marked, since a parent's `touched`
    // aggregates its descendants rather than propagating down.
    for (const field of SERVER_FIELDS) {
      this.f[field]().markAsTouched();
    }

    if (!this.canSubmit()) {
      return;
    }

    this.actions.submitForm(this.model());
  }
}

/** `headers` is one key: the API faults the whole set as `Headers`, keeping the keys flat. */
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

interface FieldErrors {
  readonly show: boolean;
  readonly messages: readonly string[];
}

function clientError(message: string | null): { kind: string; message: string } | null {
  return message === null ? null : { kind: 'format', message };
}
