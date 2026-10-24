import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * Shared parent of the grid and detail screens. Project dialogs mount in `Shell`, so browser Back
 * leaves one open over the next route, exactly as the conversation dialogs behave.
 */
@Component({
  selector: 'app-projects-layout',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet],
  // Passes the shell's `<main>` height down: the routed screen scrolls itself, which it can only do
  // inside a flex parent that may shrink below its content.
  host: { class: 'd-flex flex-column flex-fill overflow-hidden' },
  template: `<router-outlet />`,
})
export class ProjectsLayout {}
