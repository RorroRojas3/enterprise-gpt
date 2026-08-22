import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { UsageGroupDto } from '@domain/api/usage-report';
import { usageGroupFixture } from '@testing/reports';
import { UsageBarList } from './usage-bar-list';

describe('UsageBarList (US-1302)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function render(rows: UsageGroupDto[]): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(UsageBarList);
    fixture.componentRef.setInput('rows', rows);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  function widths(host: HTMLElement): string[] {
    return [...host.querySelectorAll<HTMLElement>('.bars__fill')].map((fill) => fill.style.width);
  }

  it('measures every bar against the largest row, not against the total', async () => {
    const host = await render([
      usageGroupFixture({ key: 'a', totalTokens: 18_400_000 }),
      usageGroupFixture({ key: 'b', totalTokens: 12_100_000 }),
      usageGroupFixture({ key: 'c', totalTokens: 6_800_000 }),
    ]);

    // Against the total, the leader would fill a quarter of the track and the tail would vanish.
    expect(widths(host)).toEqual(['100%', '66%', '37%']);
  });

  it('keeps a floor under a bar that would otherwise round away', async () => {
    const host = await render([
      usageGroupFixture({ key: 'a', totalTokens: 18_400_000 }),
      usageGroupFixture({ key: 'b', totalTokens: 1000 }),
    ]);

    expect(widths(host)[1]).toBe('2%');
  });

  it('numbers the rows, so the ordering survives the dark theme collapsing the two tints', async () => {
    const host = await render([
      usageGroupFixture({ key: 'a', label: 'GPT-5 Enterprise', totalTokens: 900 }),
      usageGroupFixture({ key: 'b', label: 'Claude Sonnet 4.5', totalTokens: 100 }),
    ]);
    const ranks = [...host.querySelectorAll('.bars__rank')].map((rank) => rank.textContent?.trim());

    // `--brand` and `--accent` are the same colour in the dark theme, so the board's
    // highlight-the-leader treatment has nothing left to say there.
    expect(ranks).toEqual(['1', '2']);
    expect(host.querySelectorAll('.bars__fill--leader')).toHaveLength(1);
  });

  it('states each value beside its bar, so the fill is never the only carrier', async () => {
    const host = await render([usageGroupFixture({ key: 'a', totalTokens: 18_400_000 })]);

    expect(host.querySelector('.bars__label')?.textContent).toContain('GPT-5 Enterprise');
    expect(host.querySelector('.bars__value')?.textContent?.trim()).toBe('18.4M');
  });

  it('says the range holds nothing rather than leaving a heading over an empty card', async () => {
    const host = await render([]);

    expect(host.querySelectorAll('.bars__row')).toHaveLength(0);
    expect(host.textContent).toContain('No models used in this range.');
  });

  it('draws no bar at all for a row that consumed nothing', async () => {
    const host = await render([
      usageGroupFixture({ key: 'a', totalTokens: 900 }),
      usageGroupFixture({ key: 'b', totalTokens: 0 }),
    ]);

    // The 2% floor is for a bar that rounds away, not for one that is genuinely zero.
    expect(widths(host)).toEqual(['100%', '0%']);
  });
});
