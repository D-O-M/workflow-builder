import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AbstractControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

export type DynamicFieldKind = 'string' | 'integer' | 'decimal' | 'boolean' | 'guid' | 'date' | 'datetime' | 'enum';

export interface DynamicFieldDefinition {
  key: string;
  label: string;
  kind: DynamicFieldKind;
  required: boolean;
  options?: string[];
}

@Component({
  selector: 'app-dynamic-form-field',
  imports: [
    ReactiveFormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
  ],
  template: `
    <div class="field" [formGroup]="group()">
      @if (field().kind === 'boolean' || field().kind === 'enum') {
        <mat-form-field class="field__control" appearance="outline">
          <mat-label>{{ field().label }}</mat-label>
          <mat-select [formControlName]="field().key">
            @if (!field().required) {
              <mat-option value="">No value</mat-option>
            }

            @if (field().kind === 'boolean') {
              <mat-option value="true">True</mat-option>
              <mat-option value="false">False</mat-option>
            } @else {
              @for (option of field().options ?? []; track option) {
                <mat-option [value]="option">{{ option }}</mat-option>
              }
            }
          </mat-select>

          @if (controlInvalid()) {
            <mat-error>{{ errorMessage() }}</mat-error>
          }
        </mat-form-field>
      } @else {
        <mat-form-field class="field__control" appearance="outline">
          <mat-label>{{ field().label }}</mat-label>
          <input
            matInput
            [type]="inputType()"
            [formControlName]="field().key"
            [attr.placeholder]="field().label"
            [attr.inputmode]="field().kind === 'integer' || field().kind === 'decimal' ? 'decimal' : null"
            [attr.autocomplete]="field().kind === 'guid' ? 'off' : null"
            [attr.step]="numberStep()"
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

  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DynamicFormFieldComponent {
  readonly group = input.required<FormGroup>();
  readonly field = input.required<DynamicFieldDefinition>();

  protected readonly inputType = computed(() =>
    this.field().kind === 'integer' || this.field().kind === 'decimal'
      ? 'number'
      : this.field().kind === 'date'
        ? 'date'
        : this.field().kind === 'datetime'
          ? 'datetime-local'
          : 'text',
  );

  protected readonly numberStep = computed(() =>
    this.field().kind === 'integer' ? '1' : this.field().kind === 'decimal' ? 'any' : null,
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
