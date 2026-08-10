import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ThemeService } from '@core/theme/theme-service';
import { Icon } from '@shared/icon/icon';

/**
 * The sidebar footer's theme control (US-301 places it; it lives here so US-105 has a
 * real surface and every screen can reuse it).
 *
 * The moon and sun are both rendered and swapped by `--show-light` / `--show-dark`,
 * so no script reads the theme to choose an icon. The *label* does read the resolved
 * theme, which is a different thing and necessary: a screen reader cannot see a CSS
 * custom property.
 *
 * No `aria-pressed`. The label already carries the state, and the pair announces as
 * "Switch to dark theme, not pressed", which is worse than either alone.
 */
@Component({
  selector: 'app-theme-toggle',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './theme-toggle.html',
  styleUrl: './theme-toggle.scss',
})
export class ThemeToggle {
  private readonly themeService = inject(ThemeService);

  protected readonly label = computed(() =>
    this.themeService.theme() === 'dark' ? 'Switch to light theme' : 'Switch to dark theme',
  );

  protected toggle(): void {
    this.themeService.toggle();
  }
}
