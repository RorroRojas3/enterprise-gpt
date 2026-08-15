import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * The parent of every projects screen.
 *
 * It is the shared route the area's two screens hang off — the grid and a project's
 * detail — which is why `/projects` is a `loadChildren` rather than two sibling
 * `loadComponent`s.
 *
 * **The dialogs no longer mount here.** They moved to `Shell` with US-307, whose picker
 * invokes the create flow from the composer — a surface outside this feature, on a route
 * this layout never renders. The `DestroyRef` cleanup that used to sit here went with
 * them: it existed because leaving `/projects` with a dialog open would strand `formMode`
 * in root state and re-mount the dialog already open on the way back, and `Shell` is not
 * unmounted while signed in.
 *
 * One consequence is accepted rather than overlooked: a project dialog dismissed with
 * browser **Back** now stays open over the next route, because nothing unmounts it. That
 * is exactly how the conversation dialogs have always behaved — `Modal` closes its native
 * element on destroy without emitting `closed`, and neither store is told — so US-307
 * makes the two consistent instead of leaving one of them special. A route-independent
 * cancel belongs to whichever story decides to fix both.
 */
@Component({
  selector: 'app-projects-layout',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet],
  template: `<router-outlet />`,
})
export class ProjectsLayout {}
