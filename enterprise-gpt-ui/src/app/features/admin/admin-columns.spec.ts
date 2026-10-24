import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { TableColumn } from '@shared/models/table-column.model';
import { NARROW_VIEWPORT, setMediaQuery } from '@testing/media-query';
import { BELOW_XXL, injectAdminColumns } from './admin-columns';

interface Row {
  readonly name: string;
}

const COLUMNS: readonly TableColumn<Row>[] = [
  { key: 'name', header: 'Name', width: 'minmax(0, 1fr)', mobile: 'title' },
  { key: 'url', header: 'URL', width: 'minmax(0, 1fr)', hideOnTablet: true },
  { key: 'actions', header: 'Actions', width: '44px', headerHidden: true },
];

function keys(): string[] {
  return TestBed.runInInjectionContext(() => injectAdminColumns(COLUMNS))().map(({ key }) => key);
}

describe('injectAdminColumns', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('keeps every column at the xxl edge and above', () => {
    setMediaQuery(BELOW_XXL, false);
    setMediaQuery(NARROW_VIEWPORT, false);

    expect(keys()).toEqual(['name', 'url', 'actions']);
  });

  it('drops the hideOnTablet columns below xxl, where the sidebar and rail crowd the table', () => {
    setMediaQuery(BELOW_XXL, true);
    setMediaQuery(NARROW_VIEWPORT, false);

    expect(keys()).toEqual(['name', 'actions']);
  });

  it('keeps every column on phones, whose cards have room for them', () => {
    setMediaQuery(BELOW_XXL, true);
    setMediaQuery(NARROW_VIEWPORT, true);

    expect(keys()).toEqual(['name', 'url', 'actions']);
  });

  it('follows a resize across the edge', () => {
    setMediaQuery(BELOW_XXL, false);
    setMediaQuery(NARROW_VIEWPORT, false);
    const columns = TestBed.runInInjectionContext(() => injectAdminColumns(COLUMNS));
    expect(columns()).toHaveLength(3);

    setMediaQuery(BELOW_XXL, true);

    expect(columns().map(({ key }) => key)).toEqual(['name', 'actions']);
  });
});
