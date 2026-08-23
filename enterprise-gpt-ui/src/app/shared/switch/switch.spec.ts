import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { Switch } from './switch';

describe('Switch (US-417)', () => {
  let fixture: ComponentFixture<Switch>;

  async function render(inputs: { checked: boolean }) {
    fixture = TestBed.createComponent(Switch);
    fixture.componentRef.setInput('checked', inputs.checked);
    await fixture.whenStable();
    return (fixture.nativeElement as HTMLElement).querySelector('.switch') as HTMLElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('marks the on state with a class rather than any state of its own', async () => {
    expect((await render({ checked: true })).classList.contains('switch--on')).toBe(true);
    expect((await render({ checked: false })).classList.contains('switch--on')).toBe(false);
  });

  it('exposes nothing to assistive technology', async () => {
    // The invariant the whole component rests on: it is decoration, so it can sit
    // inside a `role="menu"` panel where a real `<input role="switch">` cannot,
    // and the element that owns the semantics is never shadowed by it.
    const track = await render({ checked: true });

    expect(track.getAttribute('aria-hidden')).toBe('true');
    expect(track.getAttribute('role')).toBeNull();
    expect(track.querySelector('input')).toBeNull();
  });
});
