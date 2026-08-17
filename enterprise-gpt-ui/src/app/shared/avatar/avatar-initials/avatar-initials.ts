import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { avatarTone, initialsOf } from '../initials';

/**
 * The initials circle of frames `5a` and `5c`.
 *
 * `aria-hidden`, always: it abbreviates a name that is rendered beside it, so
 * announcing both would read the person twice — once spelled out letter by letter.
 * A caller that does *not* render the name beside it is misusing this.
 */
@Component({
  selector: 'app-avatar-initials',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span
    class="avatar"
    aria-hidden="true"
    [attr.data-tone]="tone()"
    [style.--avatar-size.px]="size()"
    >{{ initials() }}</span
  >`,
  styleUrl: './avatar-initials.scss',
})
export class AvatarInitials {
  /** The display name to abbreviate. */
  readonly name = input.required<string>();

  /** Abbreviated instead when {@link name} carries no letters — an email address. */
  readonly fallback = input<string>('');

  /**
   * What the colour is derived from. Defaults to the name, but a caller holding a
   * stable id should pass it: a rename should not recolour the row.
   */
  readonly seed = input<string>('');

  /** Diameter in pixels: 30 in the table, 40 in the detail panel. */
  readonly size = input<number>(30);

  protected readonly initials = computed(() => initialsOf(this.name(), this.fallback()));
  protected readonly tone = computed(() => avatarTone(this.seed() || this.name()));
}
