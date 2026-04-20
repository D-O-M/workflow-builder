import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AbstractControl, FormGroup, ReactiveFormsModule } from '@angular/forms';

export type DynamicFieldKind = 'string' | 'number' | 'boolean' | 'guid';

export interface DynamicFieldDefinition {
  key: string;
  label: string;
  kind: DynamicFieldKind;
  required: boolean;
}

@Component({
  selector: 'app-dynamic-form-field',
  imports: [ReactiveFormsModule],
  template: `
    <div class="field" [formGroup]="group()">
      @if (field().kind === 'boolean') {
        <label class="checkbox-field">
          <input type="checkbox" [formControlName]="field().key" />
          <span>{{ field().label }}</span>
        </label>
      } @else {
        <label class="text-field">
          <span>{{ field().label }}</span>
          <input
            [type]="inputType()"
            [formControlName]="field().key"
            [attr.placeholder]="field().label"
            [attr.inputmode]="field().kind === 'number' ? 'decimal' : null"
            [attr.autocomplete]="field().kind === 'guid' ? 'off' : null"
          />
        </label>
      }

      @if (controlInvalid()) {
        <small class="field-error">{{ errorMessage() }}</small>
      }
    </div>
  `,
  styles: [`
    .field {
      display: flex;
      flex-direction: column;
      gap: 0.35rem;
    }

    .text-field,
    .checkbox-field {
      display: flex;
      gap: 0.65rem;
      align-items: center;
      color: var(--app-text);
    }

    .text-field {
      flex-direction: column;
      align-items: stretch;
      gap: 0.35rem;
    }

    input[type='text'],
    input[type='number'] {
      border: 1px solid var(--app-border);
      border-radius: 10px;
      padding: 0.65rem 0.75rem;
      font: inherit;
      background: rgba(255, 255, 255, 0.86);
    }

    input[type='checkbox'] {
      width: 1rem;
      height: 1rem;
      accent-color: var(--app-primary);
    }

    .field-error {
      color: #b42318;
    }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DynamicFormFieldComponent {
  readonly group = input.required<FormGroup>();
  readonly field = input.required<DynamicFieldDefinition>();

  protected readonly inputType = computed(() =>
    this.field().kind === 'number' ? 'number' : 'text',
  );

  private readonly control = computed<AbstractControl | null>(() =>
    this.group().get(this.field().key),
  );

  protected readonly controlInvalid = computed(() => {
    const control = this.control();
    return !!control && control.invalid && (control.touched || control.dirty);
  });

  protected readonly errorMessage = computed(() => {
    const control = this.control();
    if (!control?.errors) {
      return '';
    }

    if (control.errors['required']) {
      return `${this.field().label} is required.`;
    }

    if (control.errors['pattern']) {
      return `${this.field().label} must be a valid GUID.`;
    }

    return `${this.field().label} is invalid.`;
  });
}
