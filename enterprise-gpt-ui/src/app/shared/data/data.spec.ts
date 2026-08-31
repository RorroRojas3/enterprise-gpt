import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  NARROW_VIEWPORT,
  TABLET_VIEWPORT_QUERY,
  resetMediaQueries,
  setMediaQuery,
} from '@testing/media-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BulkActionBar } from './bulk-action-bar/bulk-action-bar';
import { DataTable } from './data-table/data-table';
import { TableCell } from './data-table/table-cell';
import { TableColumn } from './data-table/table-column';
import { PAGE_GAP, pageWindow } from './paginator/page-window';
import { Paginator } from './paginator/paginator';
import { selectRows, toggleRow } from './row-selection';

interface Row {
  readonly id: string;
  readonly name: string;
  readonly owner: string;
}

const ROWS: readonly Row[] = [
  { id: 'a', name: 'Helios release status', owner: 'Sofía' },
  { id: 'b', name: 'Q3 revenue model', owner: 'Marco' },
];

const COLUMNS: readonly TableColumn<Row>[] = [
  { key: 'name', header: 'Name', width: '1.4fr', mobile: 'title' },
  { key: 'owner', header: 'Owner', width: '120px', text: (row) => row.owner, mobile: 'subtitle' },
];

@Component({
  selector: 'app-table-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DataTable, TableCell],
  template: `
    <app-data-table
      label="Conversations"
      [rows]="rows()"
      [columns]="columns"
      [trackKey]="trackKey"
      [rowLabel]="rowLabel"
      [loading]="loading()"
      [pendingIds]="pendingIds()"
      [selectable]="selectable()"
      [headerSelectAll]="headerSelectAll()"
      [summary]="summary()"
      [(selectedIds)]="selectedIds"
    >
      <ng-template appTableCell="name" [appTableCellFor]="rows()" let-row>
        <a class="row-name" href="#">{{ row.name }}</a>
      </ng-template>

      <p tableEmpty id="empty">Nothing here</p>
    </app-data-table>
  `,
})
class TableHost {
  // Held in readonly fields, not returned from getters: a fresh array or function on
  // every check would fail the exhaustive check-no-changes pass.
  readonly columns = COLUMNS;
  readonly trackKey = (row: Row): string => row.id;
  readonly rowLabel = (row: Row): string => row.name;

  readonly rows = signal<readonly Row[]>(ROWS);
  readonly loading = signal(false);
  readonly pendingIds = signal<ReadonlySet<string>>(new Set<string>());
  readonly selectable = signal(false);
  readonly headerSelectAll = signal(true);
  readonly summary = signal('');
  readonly selectedIds = signal<ReadonlySet<string>>(new Set<string>());
}

describe('row-selection', () => {
  it('always returns a new Set, so the signal write is actually seen', () => {
    const first = new Set(['a']);

    expect(toggleRow(first, 'b')).not.toBe(first);
    expect(selectRows(first, ['c'])).not.toBe(first);
  });

  it('toggles membership both ways', () => {
    expect([...toggleRow(new Set(), 'a')]).toEqual(['a']);
    expect([...toggleRow(new Set(['a']), 'a')]).toEqual([]);
  });
});

describe('pageWindow', () => {
  it('shows every page when they all fit', () => {
    expect(pageWindow(1, 4)).toEqual([1, 2, 3, 4]);
  });

  it('gaps the far end when the current page is at the start', () => {
    expect(pageWindow(1, 13)).toEqual([1, 2, PAGE_GAP, 13]);
  });

  it('gaps both ends when the current page is in the middle', () => {
    expect(pageWindow(7, 13)).toEqual([1, PAGE_GAP, 6, 7, 8, PAGE_GAP, 13]);
  });

  it('renders a hole of exactly one page as that page, not an ellipsis', () => {
    expect(pageWindow(4, 6)).toEqual([1, 2, 3, 4, 5, 6]);
  });

  it('handles the degenerate counts', () => {
    expect(pageWindow(1, 0)).toEqual([]);
    expect(pageWindow(1, 1)).toEqual([1]);
  });
});

describe('DataTable', () => {
  beforeEach(() => {
    resetMediaQueries();
    setMediaQuery(NARROW_VIEWPORT, false);
    TestBed.configureTestingModule({});
  });

  afterEach(() => resetMediaQueries());

  async function render() {
    const fixture = TestBed.createComponent(TableHost);
    await fixture.whenStable();
    return {
      fixture,
      host: fixture.nativeElement as HTMLElement,
      component: fixture.componentInstance,
    };
  }

  it('states every ARIA role, because display:grid strips the implicit ones', async () => {
    const { host } = await render();

    expect(host.querySelector('[role="table"]')).not.toBeNull();
    expect(host.querySelectorAll('[role="rowgroup"]')).toHaveLength(2);
    expect(host.querySelectorAll('[role="row"]')).toHaveLength(3);
    expect(host.querySelectorAll('[role="columnheader"]')).toHaveLength(2);
    expect(host.querySelectorAll('[role="cell"]')).toHaveLength(4);
  });

  it('renders a cell template with a typed row context', async () => {
    const { host } = await render();

    expect([...host.querySelectorAll('.row-name')].map((a) => a.textContent)).toEqual([
      'Helios release status',
      'Q3 revenue model',
    ]);
  });

  it('falls back to the column text function where no template matches', async () => {
    const { host } = await render();

    expect(host.textContent).toContain('Sofía');
    expect(host.textContent).toContain('Marco');
  });

  it('marks one busy row without disabling the rest of the list', async () => {
    const { fixture, host, component } = await render();

    component.pendingIds.set(new Set(['a']));
    await fixture.whenStable();

    const rows = [...host.querySelectorAll('tbody [role="row"]')];
    expect(rows[0]?.getAttribute('aria-busy')).toBe('true');
    expect(rows[1]?.getAttribute('aria-busy')).toBeNull();
  });

  it('offers real checkboxes, and select-all reports an indeterminate middle state', async () => {
    const { fixture, host, component } = await render();
    component.selectable.set(true);
    await fixture.whenStable();

    const boxes = [...host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')];
    expect(boxes).toHaveLength(3);
    expect(boxes[1]?.getAttribute('aria-label')).toBe('Select Helios release status');

    boxes[1]?.click();
    await fixture.whenStable();

    const selectAll = host.querySelector<HTMLInputElement>('thead input[type="checkbox"]');
    expect(selectAll?.indeterminate).toBe(true);
    expect([...component.selectedIds()]).toEqual(['a']);
  });

  it('drops the header select-all on request, keeping the track it sits in', async () => {
    const { fixture, host, component } = await render();
    component.selectable.set(true);
    component.headerSelectAll.set(false);
    await fixture.whenStable();

    // The caller draws select-all in its own toolbar; two controls bound to one
    // state is the defect this input exists to avoid.
    expect(host.querySelector('thead input[type="checkbox"]')).toBeNull();
    // The header cell stays, or the body rows lose the 40px track they line up on.
    expect(host.querySelectorAll('thead [role="columnheader"]')).toHaveLength(3);
    expect(host.querySelectorAll('tbody input[type="checkbox"]')).toHaveLength(2);
  });

  it('keeps selection working below the breakpoint, where there is no header', async () => {
    const { fixture, host, component } = await render();
    component.selectable.set(true);
    await fixture.whenStable();

    setMediaQuery(NARROW_VIEWPORT, true);
    await fixture.whenStable();

    // Without a checkbox on the card, a caller that set `selectable` would get a
    // selectable table on a laptop and an unselectable list on a phone.
    const boxes = [...host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')];
    expect(boxes).toHaveLength(2);
    expect(boxes[0]?.getAttribute('aria-label')).toBe('Select Helios release status');

    boxes[0]?.click();
    await fixture.whenStable();
    expect([...component.selectedIds()]).toEqual(['a']);
  });

  it('shows the projected empty state instead of an empty table', async () => {
    const { fixture, host, component } = await render();

    component.rows.set([]);
    await fixture.whenStable();

    expect(host.querySelector('#empty')).not.toBeNull();
    expect(host.querySelector('[role="table"]')).toBeNull();
  });

  it('shows skeletons while loading, and hides them from assistive technology', async () => {
    const { fixture, host, component } = await render();

    component.loading.set(true);
    await fixture.whenStable();

    expect(host.querySelector('.table-skeleton')?.getAttribute('aria-busy')).toBe('true');
    expect(host.querySelectorAll('app-skeleton').length).toBeGreaterThan(0);
    expect(host.querySelector('[role="table"]')).toBeNull();
  });

  it('swaps the table for cards below the breakpoint', async () => {
    const { fixture, host } = await render();

    setMediaQuery(NARROW_VIEWPORT, true);
    await fixture.whenStable();

    expect(host.querySelector('[role="table"]')).toBeNull();
    expect(host.querySelectorAll('app-card-row')).toHaveLength(2);
    expect(host.textContent).toContain('Helios release status');
  });

  it('announces the result count politely', async () => {
    const { host } = await render();

    const live = host.querySelector('[aria-live="polite"]');
    expect(live?.textContent).toContain('2 results');
  });

  it('lets a paginated caller replace the count with one that knows the total', async () => {
    const { fixture, host, component } = await render();

    component.summary.set('Showing 2 of 312 conversations');
    await fixture.whenStable();

    // Replaced, not appended: a screen showing its own visible counter would
    // otherwise have two disagreeing numbers announced.
    const live = host.querySelector('[aria-live="polite"]');
    expect(live?.textContent?.trim()).toBe('Showing 2 of 312 conversations');
    expect(live?.textContent).not.toContain('2 results');
  });
});

interface SlotRow {
  readonly id: string;
  readonly name: string;
  readonly updated: string;
  readonly size: string;
  readonly retired: boolean;
}

const SLOT_ROWS: readonly SlotRow[] = [
  { id: 'a', name: 'Helios', updated: '2 days ago', size: '1.4 MB', retired: false },
  { id: 'b', name: 'Atlas', updated: 'yesterday', size: '820 kB', retired: true },
];

const SLOT_COLUMNS: readonly TableColumn<SlotRow>[] = [
  { key: 'name', header: 'Name', width: '1.4fr', text: (row) => row.name, mobile: 'title' },
  { key: 'updated', header: 'Updated', width: '110px', text: (row) => row.updated, mobile: 'meta' },
  {
    key: 'size',
    header: 'Size',
    width: '90px',
    text: (row) => row.size,
    mobile: 'meta',
    hideOnTablet: true,
  },
  { key: 'menu', header: 'Actions', headerHidden: true, width: '44px', mobile: 'trailing' },
];

@Component({
  selector: 'app-slot-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DataTable, TableCell],
  template: `
    <app-data-table
      label="Documents"
      [rows]="rows"
      [columns]="columns"
      [trackKey]="trackKey"
      [rowLabel]="rowLabel"
    >
      <ng-template appTableCell="menu" [appTableCellFor]="rows" let-row>
        @if (!row.retired) {
          <button class="row-menu" type="button">More</button>
        }
      </ng-template>
    </app-data-table>
  `,
})
class SlotHost {
  readonly columns = SLOT_COLUMNS;
  readonly rows = SLOT_ROWS;
  readonly trackKey = (row: SlotRow): string => row.id;
  readonly rowLabel = (row: SlotRow): string => row.name;
}

describe('DataTable card slots', () => {
  beforeEach(() => {
    resetMediaQueries();
    setMediaQuery(NARROW_VIEWPORT, false);
    setMediaQuery(TABLET_VIEWPORT_QUERY, false);
    TestBed.configureTestingModule({});
  });

  afterEach(() => resetMediaQueries());

  async function render() {
    const fixture = TestBed.createComponent(SlotHost);
    await fixture.whenStable();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  async function renderNarrow() {
    const rendered = await render();
    setMediaQuery(NARROW_VIEWPORT, true);
    await rendered.fixture.whenStable();
    return rendered;
  }

  it('labels every meta value with the header the table would have shown', async () => {
    const { host } = await renderNarrow();

    const [first] = [...host.querySelectorAll('app-card-row')];
    const labels = [...(first?.querySelectorAll('.card-meta__label') ?? [])].map(
      (node) => node.textContent,
    );
    const values = [...(first?.querySelectorAll('.card-meta__value') ?? [])].map((node) =>
      node.textContent?.trim(),
    );

    // Without the label a card shows "2 days ago" and "1.4 MB" as two bare lines, and
    // the column header that told the reader which was which is gone.
    expect(labels).toEqual(['Updated', 'Size']);
    expect(values).toEqual(['2 days ago', '1.4 MB']);
  });

  it('puts a trailing column beside the title rather than in the action foot', async () => {
    const { host } = await renderNarrow();

    const [first] = [...host.querySelectorAll('app-card-row')];
    expect(first?.querySelector('.card-row__trailing .row-menu')).not.toBeNull();
    expect(first?.querySelector('.card-row__actions .row-menu')).toBeNull();
  });

  it('leaves a slot genuinely empty when its column renders nothing', async () => {
    const { host } = await renderNarrow();

    // The collapse itself is `:has(> :empty)` in the stylesheet, which jsdom applies no
    // layout for; what is pinned here is the precondition that rule matches on.
    const [, second] = [...host.querySelectorAll('app-card-row')];
    const projected = second?.querySelector('.card-row__trailing')?.firstElementChild;

    expect(projected).not.toBeNull();
    expect(projected?.childElementCount).toBe(0);
  });

  it('drops a hideOnTablet column from the table but keeps it on the card', async () => {
    const { fixture, host } = await render();

    setMediaQuery(TABLET_VIEWPORT_QUERY, true);
    await fixture.whenStable();

    const headers = [...host.querySelectorAll('[role="columnheader"]')].map((node) =>
      node.textContent?.trim(),
    );
    expect(headers).toEqual(['Name', 'Updated', 'Actions']);
    expect(host.textContent).not.toContain('1.4 MB');

    setMediaQuery(TABLET_VIEWPORT_QUERY, false);
    setMediaQuery(NARROW_VIEWPORT, true);
    await fixture.whenStable();

    // A phone is a different layout, not a narrower tablet: the card has room for it.
    expect(host.textContent).toContain('1.4 MB');
  });
});

describe('Paginator', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(page: number, totalPages = 13, totalCount = 312, pageSize = 25) {
    const fixture = TestBed.createComponent(Paginator);
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('totalPages', totalPages);
    fixture.componentRef.setInput('totalCount', totalCount);
    fixture.componentRef.setInput('pageSize', pageSize);
    fixture.componentRef.setInput('itemLabel', 'users');
    await fixture.whenStable();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('disables the steps at the ends for real, not just visually', async () => {
    const first = await render(1);
    const steps = [...first.host.querySelectorAll<HTMLButtonElement>('.paginator__step')];
    expect(steps[0]?.disabled).toBe(true);
    expect(steps[1]?.disabled).toBe(false);

    const last = await render(13);
    const lastSteps = [...last.host.querySelectorAll<HTMLButtonElement>('.paginator__step')];
    expect(lastSteps[1]?.disabled).toBe(true);
  });

  it('marks the current page and makes every page a button', async () => {
    const { host } = await render(7);

    expect(host.querySelector('[aria-current="page"]')?.textContent?.trim()).toBe('7');
    expect(host.querySelectorAll('.paginator__page').length).toBeGreaterThan(0);
    expect(host.querySelectorAll('span.paginator__page')).toHaveLength(0);
  });

  it('hides the ellipsis from assistive technology', async () => {
    const { host } = await render(7);

    expect(host.querySelector('.paginator__gap')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('gets the range right on a short final page', async () => {
    const { host } = await render(13, 13, 312, 25);

    expect(host.querySelector('.paginator__summary')?.textContent).toContain(
      'Showing 301–312 of 312 users',
    );
  });

  it('says so plainly when there is nothing to page through', async () => {
    const { host } = await render(1, 0, 0, 25);

    expect(host.querySelector('.paginator__summary')?.textContent).toContain('No users');
  });

  it('emits the requested page and ignores a click on the current one', async () => {
    const { fixture, host } = await render(7);
    const changed = vi.fn();
    fixture.componentInstance.pageChanged.subscribe(changed);

    host.querySelector<HTMLButtonElement>('[aria-current="page"]')?.click();
    expect(changed).not.toHaveBeenCalled();

    host.querySelector<HTMLButtonElement>('[aria-label="Next page"]')?.click();
    expect(changed).toHaveBeenCalledWith(8);
  });

  it('offers no page-size select unless a caller asks for one', async () => {
    // Frame `4a` draws none, which is why the default is empty rather than a set of
    // numbers every caller has to opt out of.
    const { host } = await render(1);

    expect(host.querySelector('.paginator__size')).toBeNull();
  });

  it('shows the size in force, even before the reader touches the control', async () => {
    const { fixture, host } = await render(1, 4, 312, 100);
    fixture.componentRef.setInput('pageSizes', [25, 50, 100]);
    await fixture.whenStable();

    // A `[value]` binding on the <select> would run before `@for` inserted the options
    // and leave the first one showing — so a deep link would display a size it is not
    // using. The selection is bound on the options for that reason.
    expect(host.querySelector<HTMLSelectElement>('.paginator__size select')?.value).toBe('100');
  });

  it('emits a chosen size and refuses one that is not on offer', async () => {
    const { fixture, host } = await render(1, 13, 312, 25);
    fixture.componentRef.setInput('pageSizes', [25, 50, 100]);
    await fixture.whenStable();

    const changed = vi.fn();
    fixture.componentInstance.pageSizeChanged.subscribe(changed);
    const select = host.querySelector<HTMLSelectElement>('.paginator__size select')!;

    select.value = '50';
    select.dispatchEvent(new Event('change'));
    expect(changed).toHaveBeenCalledWith(50);

    // The option list is the caller's; a size that is not on it must never reach a
    // request, where it would land as `take=7` or `take=NaN`.
    changed.mockClear();
    const rogue = document.createElement('option');
    rogue.value = '7';
    select.appendChild(rogue);
    select.value = '7';
    select.dispatchEvent(new Event('change'));

    expect(changed).not.toHaveBeenCalled();
  });

  it('lets a caller silence its summary when the table already announces it', async () => {
    const { fixture, host } = await render(1);
    expect(host.querySelector('.paginator__summary')?.getAttribute('aria-live')).toBe('polite');

    fixture.componentRef.setInput('liveSummary', false);
    await fixture.whenStable();

    // Two live regions carrying one sentence read the count twice on every page.
    expect(host.querySelector('.paginator__summary')?.hasAttribute('aria-live')).toBe(false);
  });
});

describe('BulkActionBar', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(count: number, note = '') {
    const fixture = TestBed.createComponent(BulkActionBar);
    fixture.componentRef.setInput('count', count);
    fixture.componentRef.setInput('note', note);
    await fixture.whenStable();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('stays out of the way until something is selected', async () => {
    const { host } = await render(0);

    expect(host.querySelector('.bulk-bar')).toBeNull();
  });

  it('announces the count politely rather than taking focus', async () => {
    const activeBefore = document.activeElement;
    const { host } = await render(24, 'Select-all covers the 24 loaded rows only');

    expect(host.querySelector('[aria-live="polite"]')?.textContent).toContain('24 selected');
    expect(host.textContent).toContain('loaded rows only');
    expect(document.activeElement).toBe(activeBefore);
  });

  it('names itself as a region so it can be reached deliberately', async () => {
    const { host } = await render(3);

    expect(host.querySelector('[role="region"]')?.getAttribute('aria-label')).toBe('Bulk actions');
  });
});
