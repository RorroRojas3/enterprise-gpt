import { TestBed } from '@angular/core/testing';
import { getByText } from '@testing-library/dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { StatusDot } from './status-dot';

describe('StatusDot', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(inputs: { label: string; tone?: string; labelHidden?: boolean }) {
    const fixture = TestBed.createComponent(StatusDot);
    fixture.componentRef.setInput('label', inputs.label);
    if (inputs.tone) fixture.componentRef.setInput('tone', inputs.tone);
    if (inputs.labelHidden !== undefined) {
      fixture.componentRef.setInput('labelHidden', inputs.labelHidden);
    }
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return { host, mark: host.querySelector<HTMLElement>('[aria-hidden="true"]') };
  }

  it('keeps the meaning in text, so colour is never the only channel', async () => {
    const { host, mark } = await render({ label: 'Active', tone: 'success' });

    expect(getByText(host, 'Active')).not.toBeNull();
    expect(mark?.textContent).toBe('');
    expect(mark?.classList.contains('bg-success')).toBe(true);
  });

  it('hides the label visually without removing it from the accessibility tree', async () => {
    const { host } = await render({ label: 'Active', labelHidden: true });

    expect(getByText(host, 'Active').classList.contains('visually-hidden')).toBe(true);
  });

  it.each(['provider-azure-openai', 'provider-bedrock', 'provider-anthropic'])(
    'renders the %s provider tone the model picker uses',
    async (tone) => {
      const { mark } = await render({ label: '', tone, labelHidden: true });

      expect(mark?.style.backgroundColor).toBe(`var(--${tone})`);
      expect([...(mark?.classList ?? [])].some((name) => name.startsWith('bg-'))).toBe(false);
    },
  );
});
