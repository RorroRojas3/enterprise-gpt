import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * The `ToolKind` badge on an activity card. Renders the kind alone, so a card reads
 * "Jira Cloud" + "MCP tool" rather than "Calling Jira Cloud MCP".
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
    // An unknown kind renders as itself rather than as nothing.
    return KIND_LABELS[kind] ?? titleCase(kind);
  });
}

// `Unknown` is the kind reasoning phases stream under, so it renders as "Reasoning".
const KIND_LABELS: Readonly<Record<string, string>> = {
  Function: 'Function',
  McpTool: 'MCP tool',
  Agent: 'Agent',
  Unknown: 'Reasoning',
};

function titleCase(value: string): string {
  // charAt, not value[0], which is possibly undefined under noUncheckedIndexedAccess.
  return value.charAt(0).toUpperCase() + value.slice(1);
}
