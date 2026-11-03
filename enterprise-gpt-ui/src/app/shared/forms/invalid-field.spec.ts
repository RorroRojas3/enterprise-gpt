import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { FieldErrors } from './field-errors';
import { InvalidField } from './invalid-field';

@Component({
  selector: 'app-field-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FieldErrors, InvalidField],
  template: `
    <input
      id="name"
      [appInvalidField]="invalid()"
      [fieldErrorsShown]="regionShown()"
      fieldNote="name-note"
    />
    <input id="plain" [appInvalidField]="invalid()" />
    <app-field-errors fieldId="name" [errors]="{ show: invalid(), messages: ['One', 'One'] }" />
  `,
})
class FieldHost {
  readonly invalid = signal(false);
  readonly regionShown = signal<boolean | undefined>(undefined);
}

describe('InvalidField and FieldErrors', () => {
  async function render() {
    const fixture = TestBed.createComponent(FieldHost);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;

    return {
      fixture,
      host,
      name: host.querySelector('#name')!,
      plain: host.querySelector('#plain')!,
    };
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('says nothing about a valid control beyond its note', async () => {
    const { host, name, plain } = await render();

    expect(name.hasAttribute('aria-invalid')).toBe(false);
    expect(name.classList.contains('is-invalid')).toBe(false);
    expect(name.getAttribute('aria-describedby')).toBe('name-note');
    expect(plain.hasAttribute('aria-describedby')).toBe(false);
    expect(host.querySelector('[role="alert"]')).toBeNull();
  });

  it('marks an invalid control and points it at the errors that render', async () => {
    const { fixture, host, name, plain } = await render();

    fixture.componentInstance.invalid.set(true);
    await fixture.whenStable();

    expect(name.getAttribute('aria-invalid')).toBe('true');
    expect(name.classList.contains('is-invalid')).toBe(true);
    expect(name.getAttribute('aria-describedby')).toBe('name-errors name-note');
    expect(plain.getAttribute('aria-describedby')).toBe('plain-errors');

    const region = host.querySelector('#name-errors');
    expect(region?.getAttribute('role')).toBe('alert');
    // Two rules can emit the same sentence.
    expect(region?.querySelectorAll('p')).toHaveLength(2);
  });

  it('describes a region that shows without the field itself being invalid', async () => {
    const { fixture, name } = await render();

    fixture.componentInstance.regionShown.set(true);
    await fixture.whenStable();

    expect(name.hasAttribute('aria-invalid')).toBe(false);
    expect(name.getAttribute('aria-describedby')).toBe('name-errors name-note');
  });
});
