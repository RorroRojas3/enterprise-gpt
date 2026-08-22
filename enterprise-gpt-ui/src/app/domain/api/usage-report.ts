/**
 * The aggregated usage report, as `GET api/reports/usage` returns it (US-1301).
 *
 * One request serves the whole dashboard: period totals, the prior period beside them, the
 * outcome split, a dense daily series, the grouped breakdown and the fixed top-ten models.
 * Nothing here is cached — `ReportsStore` is route-scoped, so every entry to the tab issues a
 * fresh request (FR-41).
 *
 * Every figure counts **both** kinds of call, a chat turn and the out-of-band naming completion
 * alike, because both are billed. `namingTurns` and `namingTokens` carry that overhead separately
 * for anyone who needs to see it apart.
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
  /**
   * The ten heaviest models, whatever {@link groupBy} says.
   *
   * The bar chart asks a fixed question — which models cost the most — that does not change when
   * the table beside it is re-cut, so the server answers both in one response. With
   * `groupBy=model` this is the head of {@link groups}.
   */
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
   * What the transcribed messages weigh, estimated server-side.
   *
   * Reported beside the billed columns and never inside them: this estimates what the history
   * costs to replay, not what was charged, and the two are supposed to disagree.
   */
  readonly contextTokens: number;
  /**
   * USD, or `null` when nothing in the period could be priced.
   *
   * Null is not zero. A period whose models carry no price answers null, and rendering that as
   * `$0` would read as free. {@link unpricedTurns} is what says the figure is a floor.
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
