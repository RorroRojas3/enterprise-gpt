import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * The `ToolKind` badge on an activity card — "MCP tool", "Agent", "Function".
 *
 * It renders the kind **alone**. The contract keeps `displayName` and `kind` apart
 * precisely so a card reads "Jira Cloud" + "MCP tool" rather than "Calling Jira Cloud
 * MCP", and composing them here would put the kind word back.
 */
@Component({
  selector: 'app-kind-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<span class="kind-badge">{{ label() }}</span>',
  styleUrl: './kind-badge.scss',
})
export class KindBadge {
  /** A `ToolKind` value from the stream contract, or any other short category. */
  readonly kind = input.required<string>();

  protected readonly label = computed(() => {
    const kind = this.kind();
    // The `??` is load-bearing under noUncheckedIndexedAccess, and it is also the
    // honest behaviour: an unknown kind renders as itself rather than as nothing.
    return KIND_LABELS[kind] ?? titleCase(kind);
  });
}

// US-501 pins the badge to exactly four values — Reasoning, Function, MCP tool,
// Agent — one per `ToolKind` arm, so `Unknown` (the kind reasoning phases stream
// under) renders as "Reasoning" rather than a fifth word the design lacks.
const KIND_LABELS: Readonly<Record<string, string>> = {
  Function: 'Function',
  McpTool: 'MCP tool',
  Agent: 'Agent',
  Unknown: 'Reasoning',
};

function titleCase(value: string): string {
  // charAt rather than value[0], which noUncheckedIndexedAccess types as possibly
  // undefined and would render the literal string "undefined" if it ever were.
  return value.charAt(0).toUpperCase() + value.slice(1);
}
