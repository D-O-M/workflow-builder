import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AbstractControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { taskPropertyInputHint, taskPropertyValidationMessage } from '../domain/task-property-form-utils';

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
    MatDatepickerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
  ],
  providers: [provideNativeDateAdapter()],
  template: `
    <div class="field" [formGroup]="group()">
      @if (field().kind === 'date') {
        <mat-form-field class="field__control" appearance="outline">
          <mat-label>{{ field().label }}</mat-label>
          <input
            matInput
            [matDatepicker]="datePicker"
            [formControlName]="field().key"
            [attr.placeholder]="field().label"
            autocomplete="off"
          />
          <mat-datepicker-toggle matIconSuffix [for]="datePicker" />
          <mat-datepicker #datePicker />

          @if (hint()) {
            <mat-hint>{{ hint() }}</mat-hint>
          }

          @if (controlInvalid()) {
            <mat-error>{{ errorMessage() }}</mat-error>
          }
        </mat-form-field>
      } @else if (field().kind === 'boolean' || field().kind === 'enum') {
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

          @if (inputIcon()) {
            <mat-icon matSuffix>{{ inputIcon() }}</mat-icon>
          }

          @if (hint()) {
            <mat-hint>{{ hint() }}</mat-hint>
          }

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
            [attr.inputmode]="inputMode()"
            [attr.autocomplete]="autocomplete()"
            [attr.step]="numberStep()"
          />

          @if (inputIcon()) {
            <mat-icon matSuffix>{{ inputIcon() }}</mat-icon>
          }

          @if (hint()) {
            <mat-hint>{{ hint() }}</mat-hint>
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
    this.field().kind === 'date'
        ? 'date'
        : this.field().kind === 'datetime'
          ? 'datetime-local'
          : 'text',
  );

  protected readonly numberStep = computed(() =>
    this.field().kind === 'datetime' ? '60' : null,
  );

  protected readonly inputMode = computed(() =>
    this.field().kind === 'integer'
      ? 'numeric'
      : this.field().kind === 'decimal'
        ? 'decimal'
        : null,
  );

  protected readonly autocomplete = computed(() =>
    this.field().kind === 'guid' ? 'off' : null,
  );

  protected readonly inputIcon = computed(() => {
    switch (this.field().kind) {
      case 'integer':
      case 'decimal':
        return 'tag';
      case 'boolean':
        return 'toggle_on';
      case 'guid':
        return 'fingerprint';
      case 'date':
        return 'calendar_month';
      case 'datetime':
        return 'event_available';
      case 'enum':
        return 'tune';
      default:
        return null;
    }
  });

  protected readonly hint = computed(() =>
    taskPropertyInputHint(this.field()),
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

    return taskPropertyValidationMessage(this.field(), control.errors);
  });
}
