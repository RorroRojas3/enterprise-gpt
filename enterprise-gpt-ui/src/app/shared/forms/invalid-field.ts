import { Directive, ElementRef, computed, inject, input } from '@angular/core';

/** States a control's invalidity to every reader. Its error text is {@link FieldErrors}. */
@Directive({
  selector: '[appInvalidField]',
  host: {
    '[class.is-invalid]': 'appInvalidField()',
    '[attr.aria-invalid]': "appInvalidField() ? 'true' : null",
    '[attr.aria-describedby]': 'describedBy()',
  },
})
export class InvalidField {
  private readonly _host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  readonly appInvalidField = input.required<boolean>();
  /** For a region that also carries messages the field itself does not fail on. */
  readonly fieldErrorsShown = input<boolean>();
  /** The id of a note read out on every focus, such as a counter. */
  readonly fieldNote = input<string>();

  protected readonly describedBy = computed(() => {
    const ids = [
      (this.fieldErrorsShown() ?? this.appInvalidField()) ? `${this._host.id}-errors` : null,
      this.fieldNote() ?? null,
    ].filter((id) => id !== null);

    return ids.length > 0 ? ids.join(' ') : null;
  });
}
