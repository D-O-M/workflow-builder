import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AbstractControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

export type DynamicFieldKind = 'string' | 'number' | 'boolean' | 'guid';

export interface DynamicFieldDefinition {
  key: string;
  label: string;
  kind: DynamicFieldKind;
  required: boolean;
}

@Component({
  selector: 'app-dynamic-form-field',
  imports: [
    ReactiveFormsModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  template: `
    <div class="field" [formGroup]="group()">
      @if (field().kind === 'boolean') {
        <div class="field__boolean">
          <mat-checkbox [formControlName]="field().key">
            {{ field().label }}
          </mat-checkbox>

          @if (controlInvalid()) {
            <p class="field__error">{{ errorMessage() }}</p>
          }
        </div>
      } @else {
        <mat-form-field class="field__control" appearance="outline">
          <mat-label>{{ field().label }}</mat-label>
          <input
            matInput
            [type]="inputType()"
            [formControlName]="field().key"
            [attr.placeholder]="field().label"
            [attr.inputmode]="field().kind === 'number' ? 'decimal' : null"
            [attr.autocomplete]="field().kind === 'guid' ? 'off' : null"
            [attr.step]="field().kind === 'number' ? 'any' : null"
          />

          @if (field().kind === 'guid') {
            <mat-hint>GUID format expected.</mat-hint>
          }

          @if (controlInvalid()) {
            <mat-error>{{ errorMessage() }}</mat-error>
          }
        </mat-form-field>
      }
    </div>
  `,
  styles: [`
    .field {
      width: 100%;
    }

    .field__control {
      width: 100%;
    }

    .field__boolean {
      display: grid;
      gap: 0.5rem;
      padding: 0.5rem 0;
    }

    .field__error {
      margin: 0;
      color: var(--app-status-danger);
      font-size: 0.83rem;
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
