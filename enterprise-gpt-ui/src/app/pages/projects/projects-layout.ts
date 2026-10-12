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
  template: `<router-outlet />`,
})
export class ProjectsLayout {}
