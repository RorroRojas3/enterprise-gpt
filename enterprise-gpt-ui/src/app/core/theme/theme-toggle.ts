import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Icon } from '@shared/icons/icon';
import { Theme } from './theme';

/**
 * CSS tokens swap the icons, but the label reads the resolved theme: a screen reader cannot see a
 * custom property. No `aria-pressed`: beside the label it announces "…, not pressed".
 */
@Component({
  selector: 'app-theme-toggle',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './theme-toggle.html',
})
export class ThemeToggle {
  private readonly themeService = inject(Theme);

  protected readonly label = computed(() =>
    this.themeService.theme() === 'dark' ? 'Switch to light theme' : 'Switch to dark theme',
  );

  protected toggle(): void {
    this.themeService.toggle();
  }
}
