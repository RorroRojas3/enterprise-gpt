import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { iconOf } from '@testing/overlays';
import { ActivityIndicator, ConversationActivity } from './activity-indicator';

describe('ActivityIndicator', () => {
  async function render(activity: ConversationActivity, labelled = true): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(ActivityIndicator);
    fixture.componentRef.setInput('activity', activity);
    fixture.componentRef.setInput('labelled', labelled);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('spins while an answer is generating, saying so in text', async () => {
    const host = await render('running');

    expect(host.querySelector('.spinner-border')?.getAttribute('aria-hidden')).toBe('true');
    expect(host.textContent?.trim()).toBe('Generating answer');
  });

  it('marks an unseen answer with a dot and its meaning', async () => {
    const host = await render('answer');

    expect(host.querySelector('app-status-dot .bg-primary')).not.toBeNull();
    expect(host.querySelector('.visually-hidden:not(app-status-dot *)')?.textContent).toBe(
      'New answer',
    );
  });

  it('warns of an interrupted answer', async () => {
    const host = await render('interrupted');

    expect(iconOf(host)).toBe('bi-exclamation-triangle-fill');
    expect(host.textContent?.trim()).toBe('Response interrupted');
  });

  it('drops its own label where the host control already names the state', async () => {
    const host = await render('running', false);

    expect(host.textContent?.trim()).toBe('');
  });
});
