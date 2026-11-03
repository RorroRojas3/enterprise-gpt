import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export interface FieldErrorState {
  readonly show: boolean;
  readonly messages: readonly string[];
}

/** A field's error text, under the id {@link InvalidField} points `aria-describedby` at. */
@Component({
  selector: 'app-field-errors',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'd-block' },
  template: `
    @if (errors().show) {
      <!-- role="alert", since focus does not move on submit. track $index: two rules can emit
           identical text. -->
      <div class="invalid-feedback d-block" [id]="fieldId() + '-errors'" role="alert">
        @for (message of errors().messages; track $index) {
          <p class="mb-0">{{ message }}</p>
        }
      </div>
    }
  `,
})
export class FieldErrors {
  /** The id of the control these errors belong to. */
  readonly fieldId = input.required<string>();
  readonly errors = input.required<FieldErrorState>();
}
