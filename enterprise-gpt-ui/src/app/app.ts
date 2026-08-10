import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ToastRegion } from '@shared/feedback/toast/toast-region';
import { ThemeToggle } from '@shared/theme-toggle/theme-toggle';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ThemeToggle, ToastRegion],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The header is a temporary host for the theme control so US-105 is verifiable
  // before a shell exists. US-301 moves <app-theme-toggle> into the sidebar footer
  // and deletes the header.
  //
  // <app-toast-region> is mounted here permanently, and once: its two live regions
  // have to exist in the DOM before the first toast, or the first one is never
  // announced.
  template: `
    <header class="dev-shell-bar"><app-theme-toggle /></header>
    <router-outlet />
    <app-toast-region />
  `,
  styles: `
    .dev-shell-bar {
      display: flex;
      justify-content: flex-end;
      padding: 10px 16px;
    }
  `,
})
export class App {}
