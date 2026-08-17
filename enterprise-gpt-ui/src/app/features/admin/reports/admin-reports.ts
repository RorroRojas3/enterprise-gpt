import { ChangeDetectionStrategy, Component } from '@angular/core';
import { UnavailablePanel } from '@shared/feedback/unavailable-panel/unavailable-panel';

/**
 * The administration usage reports tab, frame `5i` (US-1209).
 *
 * **The fourth tab exists so that its URL does**, which is this story's first criterion:
 * `/admin/reports` has to render as the active tab with the rail marking it, and a tab
 * whose route is missing cannot be marked. US-1302 replaces the panel below with frames
 * `5j`/`5k`'s dashboard once US-1301 puts `GET api/reports/usage` on the wire; nothing
 * else on this route changes when it does.
 *
 * **No store, and deliberately so.** There is no endpoint to call, so a `ReportsStore`
 * here would hold no state, expose no method and load nothing — and US-1209's third
 * criterion, that opening a tab instantiates that tab's store and no other, is satisfied
 * most plainly by a tab that instantiates none. `ReportsStore` arrives with the endpoint.
 *
 * It renders no rows, no totals and no sample chart: FR-49 is the reason `UnavailablePanel`
 * exists, and a dashboard drawn from placeholder figures is indistinguishable from one
 * drawn from a quiet month.
 *
 * One departure from frame `5i`, which draws the panel alone in the content area: the
 * `<h1>`. Every sibling tab carries one, and the panel's own heading is a `<p>` bound
 * through `aria-labelledby` rather than a heading role — so without it this would be the
 * only screen in the app with no level-one heading, which is a gap US-1401's axe gate
 * would find rather than a fidelity win.
 */
@Component({
  selector: 'app-admin-reports',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [UnavailablePanel],
  templateUrl: './admin-reports.html',
  styleUrl: './admin-reports.scss',
})
export class AdminReports {}
