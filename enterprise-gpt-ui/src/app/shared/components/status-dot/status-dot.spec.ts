import { TestBed } from '@angular/core/testing';
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
    return fixture.nativeElement as HTMLElement;
  }

  it('keeps the meaning in text, so colour is never the only channel', async () => {
    const host = await render({ label: 'Active', tone: 'ok' });

    expect(host.textContent).toContain('Active');
    expect(host.querySelector('.status-dot__mark')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('hides the label visually without removing it from the accessibility tree', async () => {
    const host = await render({ label: 'Active', labelHidden: true });

    const label = host.querySelector('.visually-hidden');
    expect(label?.textContent).toContain('Active');
  });

  it.each(['provider-azure-openai', 'provider-bedrock', 'provider-anthropic'])(
    'renders the %s provider tone the model picker uses',
    async (tone) => {
      const host = await render({ label: '', tone, labelHidden: true });

      expect(host.querySelector(`.status-dot__mark--${tone}`)).not.toBeNull();
    },
  );
});
