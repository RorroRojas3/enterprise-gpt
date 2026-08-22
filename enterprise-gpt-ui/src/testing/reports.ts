import {
  UsageGroupDto,
  UsageReportDto,
  UsageReportTotalsDto,
  UsageSeriesPointDto,
} from '../app/domain/api/usage-report';

/** Period figures, with every field explicit so a fixture cannot pass against a shape the API never sends. */
export function usageTotalsFixture(
  overrides: Partial<UsageReportTotalsDto> = {},
): UsageReportTotalsDto {
  return {
    turns: 2193,
    completedTurns: 1710,
    cancelledTurns: 285,
    failedTurns: 198,
    namingTurns: 120,
    namingTokens: 240_000,
    assistantTokens: 44_000_000,
    toolTokens: 4_200_000,
    totalTokens: 48_200_000,
    contextTokens: 12_000_000,
    estimatedCost: 1284,
    unpricedTurns: 0,
    activeUsers: 216,
    conversations: 1842,
    ...overrides,
  };
}

export function usageGroupFixture(overrides: Partial<UsageGroupDto> = {}): UsageGroupDto {
  return {
    key: '11111111-2222-4333-8444-555555555555',
    label: 'GPT-5 Enterprise',
    sublabel: null,
    turns: 812,
    completedTurns: 741,
    cancelledTurns: 48,
    failedTurns: 23,
    assistantTokens: 17_000_000,
    toolTokens: 1_400_000,
    totalTokens: 18_400_000,
    estimatedCost: 512,
    unpricedTurns: 0,
    ...overrides,
  };
}

/** A dense run of days, the way the server builds one — every day present, quiet ones at zero. */
export function usageSeriesFixture(days = 30, from = '2026-07-09'): UsageSeriesPointDto[] {
  const start = new Date(`${from}T00:00:00Z`);

  return Array.from({ length: days }, (_, index) => {
    const day = new Date(start.getTime() + index * 86_400_000);

    return {
      date: day.toISOString().slice(0, 10),
      totalTokens: index % 5 === 0 ? 0 : (index + 1) * 100_000,
      estimatedCost: index % 5 === 0 ? null : index + 1,
    };
  });
}

/** A whole report, as `GET api/reports/usage` returns one. */
export function usageReportFixture(overrides: Partial<UsageReportDto> = {}): UsageReportDto {
  const groups = overrides.groups ?? [
    usageGroupFixture(),
    usageGroupFixture({
      key: 'b',
      label: 'Claude Sonnet 4.5',
      totalTokens: 12_100_000,
      turns: 486,
    }),
    usageGroupFixture({ key: 'c', label: 'GPT-5 Mini', totalTokens: 6_800_000, turns: 402 }),
  ];

  return {
    from: '2026-07-09T12:00:00+00:00',
    to: '2026-08-08T12:00:00+00:00',
    groupBy: 'model',
    totals: usageTotalsFixture(),
    priorTotals: usageTotalsFixture({
      totalTokens: 43_000_000,
      estimatedCost: 1189,
      activeUsers: 223,
      conversations: 1602,
    }),
    outcomes: [
      { status: 'Completed', turns: 1710, totalTokens: 37_600_000 },
      { status: 'Cancelled', turns: 285, totalTokens: 6_300_000 },
      { status: 'Failed', turns: 198, totalTokens: 4_300_000 },
    ],
    series: usageSeriesFixture(),
    groups,
    topModels: groups,
    groupsTruncated: false,
    groupLimit: 500,
    ...overrides,
  };
}

/** A report with nothing in it, which is what an empty range answers. */
export function emptyUsageReportFixture(): UsageReportDto {
  const zeroes = usageTotalsFixture({
    turns: 0,
    completedTurns: 0,
    cancelledTurns: 0,
    failedTurns: 0,
    namingTurns: 0,
    namingTokens: 0,
    assistantTokens: 0,
    toolTokens: 0,
    totalTokens: 0,
    contextTokens: 0,
    estimatedCost: null,
    unpricedTurns: 0,
    activeUsers: 0,
    conversations: 0,
  });

  return usageReportFixture({
    totals: zeroes,
    priorTotals: zeroes,
    outcomes: [],
    groups: [],
    topModels: [],
    series: usageSeriesFixture(7),
  });
}
