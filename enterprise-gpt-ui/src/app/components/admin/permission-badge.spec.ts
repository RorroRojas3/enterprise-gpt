import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { PermissionBadge } from './permission-badge';

describe('PermissionBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it('spells out why a grant is locked, rather than relying on the padlock', async () => {
    const fixture = TestBed.createComponent(PermissionBadge);
    fixture.componentRef.setInput('name', 'Upload File');
    fixture.componentRef.setInput('managedBy', 'Jira Cloud MCP');
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.visually-hidden')?.textContent).toContain('Jira Cloud MCP');
    expect(host.querySelector('app-icon')).not.toBeNull();
  });

  it('shows no padlock for a grant an administrator can revoke', async () => {
    const fixture = TestBed.createComponent(PermissionBadge);
    fixture.componentRef.setInput('name', 'Upload File');
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('app-icon')).toBeNull();
  });
});
