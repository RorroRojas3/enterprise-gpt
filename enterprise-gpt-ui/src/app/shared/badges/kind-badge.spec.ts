import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { KindBadge } from './kind-badge';

describe('KindBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(kind: string) {
    const fixture = TestBed.createComponent(KindBadge);
    fixture.componentRef.setInput('kind', kind);
    await fixture.whenStable();
    return (fixture.nativeElement as HTMLElement).textContent?.trim();
  }

  it.each([
    ['McpTool', 'MCP tool'],
    ['Agent', 'Agent'],
    ['Function', 'Function'],
    // Unknown is the reasoning arm.
    ['Unknown', 'Reasoning'],
  ])('renders %s as %s', async (kind, label) => {
    expect(await render(kind)).toBe(label);
  });

  it('renders a kind it has never seen rather than nothing', async () => {
    expect(await render('somethingNew')).toBe('SomethingNew');
  });
});
