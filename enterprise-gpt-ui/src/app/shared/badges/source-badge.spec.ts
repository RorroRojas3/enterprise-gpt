import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { SourceBadge } from './source-badge';

describe('SourceBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it.each([
    ['uploaded', 'Uploaded'],
    ['generated', 'Generated'],
  ])('says %s in words, not only in colour', async (source, label) => {
    const fixture = TestBed.createComponent(SourceBadge);
    fixture.componentRef.setInput('source', source);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).textContent?.trim()).toBe(label);
  });
});
