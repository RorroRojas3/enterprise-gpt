import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { avatarTone, initialsOf } from '@shared/utils/text/initials';

/**
 * Always `aria-hidden`, or the name rendered beside it would be read twice. A caller must render
 * that name.
 */
@Component({
  selector: 'app-avatar-initials',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'd-inline-flex' },
  template: `<span
    class="avatar avatar-circle fw-semibold lh-1 user-select-none"
    aria-hidden="true"
    [attr.data-tone]="tone()"
    [style.--avatar-size.px]="size()"
    >{{ initials() }}</span
  >`,
  styleUrl: './avatar-initials.scss',
})
export class AvatarInitials {
  readonly name = input.required<string>();

  /** Abbreviated instead when {@link name} carries no letters, e.g. an email address. */
  readonly fallback = input<string>('');

  /** Colour seed, defaulting to the name; pass a stable id so a rename does not recolour. */
  readonly seed = input<string>('');

  /** Diameter in pixels: 30 in the table, 40 in the detail panel. */
  readonly size = input<number>(30);

  protected readonly initials = computed(() => initialsOf(this.name(), this.fallback()));
  protected readonly tone = computed(() => avatarTone(this.seed() || this.name()));
}
