/**
 * One request serves the whole dashboard. Every figure counts chat turns and naming completions
 * alike, since both are billed; `namingTurns` and `namingTokens` show that overhead apart.
 */
export interface UsageReportDto {
  /** The inclusive start of the window the server actually measured. */
  readonly from: string;
  /** The exclusive end of the window the server actually measured. */
  readonly to: string;
  /** The grouping the server applied: `model`, `provider`, `user` or `day`. */
  readonly groupBy: string;
  readonly totals: UsageReportTotalsDto;
  /** The equal-length window immediately before {@link from}, for the KPI deltas. */
  readonly priorTotals: UsageReportTotalsDto;
  /** One entry per outcome that occurred. An outcome with no calls is absent, not zero. */
  readonly outcomes: readonly UsageOutcomeDto[];
  /** One entry per UTC day in the window, including the quiet ones. */
  readonly series: readonly UsageSeriesPointDto[];
  /** The breakdown the table renders, heaviest first. */
  readonly groups: readonly UsageGroupDto[];
  /** The ten heaviest models, whatever {@link groupBy} says: the chart's question is fixed. */
  readonly topModels: readonly UsageGroupDto[];
  /** Whether {@link groups} was cut short at {@link groupLimit}. */
  readonly groupsTruncated: boolean;
  readonly groupLimit: number;
}

/** Aggregate figures for one period or one group. */
export interface UsageReportTotalsDto {
  /** Always the sum of the three outcome counts below. */
  readonly turns: number;
  readonly completedTurns: number;
  readonly cancelledTurns: number;
  readonly failedTurns: number;
  /** The subset of {@link turns} that named a conversation rather than answering a prompt. */
  readonly namingTurns: number;
  readonly namingTokens: number;
  /** Tokens the assistant's own model turns consumed, tools excluded. */
  readonly assistantTokens: number;
  /** Tokens consumed by every tool those turns invoked. */
  readonly toolTokens: number;
  /** The billed total: {@link assistantTokens} plus {@link toolTokens}. */
  readonly totalTokens: number;
  /**
   * Estimated cost of replaying the history, not what was charged: kept out of the billed
   * columns, and expected to disagree with them.
   */
  readonly contextTokens: number;
  /**
   * USD, or `null` when nothing could be priced; never render null as `$0`.
   * {@link unpricedTurns} says when the figure is a floor.
   */
  readonly estimatedCost: number | null;
  readonly unpricedTurns: number;
  readonly activeUsers: number;
  /** Conversations *used* in the period, which is not conversations created in it. */
  readonly conversations: number;
}

/** One row of the grouped breakdown. */
export interface UsageGroupDto {
  /** A model, provider or user id, or an ISO-8601 date — stable across a rename. */
  readonly key: string;
  /** What to show. The catalog's current name, while the figures are the snapshot. */
  readonly label: string;
  /** The email, when grouping by user, because display names collide. Null otherwise. */
  readonly sublabel: string | null;
  readonly turns: number;
  readonly completedTurns: number;
  readonly cancelledTurns: number;
  readonly failedTurns: number;
  readonly assistantTokens: number;
  readonly toolTokens: number;
  readonly totalTokens: number;
  readonly estimatedCost: number | null;
  readonly unpricedTurns: number;
}

/** How many calls ended one way, and what they consumed. */
export interface UsageOutcomeDto {
  /** `Completed`, `Cancelled` or `Failed` — the server sends the name, not the stored number. */
  readonly status: string;
  readonly turns: number;
  readonly totalTokens: number;
}

/** One day of the reported window. */
export interface UsageSeriesPointDto {
  /** `yyyy-MM-dd`, UTC. */
  readonly date: string;
  readonly totalTokens: number;
  readonly estimatedCost: number | null;
}
