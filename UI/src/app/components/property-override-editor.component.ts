import {
  ChangeDetectionStrategy,
  Component,
  effect,
  input,
  output,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { DynamicFieldDefinition } from './dynamic-form-field.component';
import { TaskPropertyType } from '../domain/node-model';
import {
  createTaskPropertyControl,
  createTaskPropertyFieldDefinitions,
  taskPropertyKindLabel,
} from '../domain/task-property-form-utils';

export interface PropertyOverrideEditorState {
  editorId: string;
  overrides: Record<string, unknown>;
  valid: boolean;
}

@Component({
  selector: 'app-property-override-editor',
  imports: [
    ReactiveFormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
  ],
  template: `
    <div class="override-editor-shell">
      @if (fields.length === 0) {
        <p class="override-editor-shell__empty">{{ emptyMessage() }}</p>
      } @else {
        <p class="override-editor-shell__support">
          Leave a field blank to keep the blueprint value.
        </p>

        <div class="override-editor-shell__rows" [formGroup]="form">
          @for (field of fields; track field.key) {
            <div class="override-editor-shell__row">
              <div class="override-editor-shell__row-header">
                <label class="override-editor-shell__label" [attr.for]="inputId(field)">
                  {{ field.label }}
                </label>
                <span class="override-editor-shell__kind">{{ kindLabel(field) }}</span>
              </div>

              @if (field.kind === 'boolean') {
                <mat-form-field class="override-editor-shell__control" appearance="outline" subscriptSizing="dynamic">
                  <mat-select [id]="inputId(field)" [formControlName]="field.key">
                    <mat-option value="">Use blueprint value</mat-option>
                    <mat-option value="true">True</mat-option>
                    <mat-option value="false">False</mat-option>
                  </mat-select>
                </mat-form-field>
              } @else {
                <mat-form-field class="override-editor-shell__control" appearance="outline" subscriptSizing="dynamic">
                  <input
                    matInput
                    [id]="inputId(field)"
                    [type]="inputType(field)"
                    [formControlName]="field.key"
                    [attr.inputmode]="field.kind === 'number' ? 'decimal' : null"
                    [attr.autocomplete]="field.kind === 'guid' ? 'off' : null"
                    [attr.step]="field.kind === 'number' ? 'any' : null"
                    placeholder="Use blueprint value"
                  />
                </mat-form-field>
              }

              @if (field.kind === 'guid') {
                <p class="override-editor-shell__hint">GUID format expected when provided.</p>
              }

              @if (controlInvalid(field.key)) {
                <p class="override-editor-shell__error">{{ errorMessage(field) }}</p>
              }
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .override-editor-shell {
      display: grid;
      gap: 1rem;
    }

    .override-editor-shell__empty,
    .override-editor-shell__support {
      margin: 0;
      color: var(--app-text-secondary);
      font-size: 0.92rem;
    }

    .override-editor-shell__rows {
      display: grid;
      gap: 1rem;
    }

    .override-editor-shell__row {
      display: grid;
      gap: 0.5rem;
      padding: 1rem 0;
      border-bottom: 1px solid rgba(102, 129, 170, 0.12);
    }

    .override-editor-shell__row:last-child {
      padding-bottom: 0;
      border-bottom: 0;
    }

    .override-editor-shell__row-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 1rem;
      flex-wrap: wrap;
    }

    .override-editor-shell__label {
      color: var(--app-text);
      font-size: 0.95rem;
      font-weight: 700;
      line-height: 1.35;
    }

    .override-editor-shell__kind {
      color: var(--app-text-muted);
      font-size: 0.78rem;
      font-weight: 700;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      white-space: nowrap;
    }

    .override-editor-shell__control {
      width: 100%;
    }

    .override-editor-shell__hint,
    .override-editor-shell__error {
      margin: 0;
      font-size: 0.84rem;
      line-height: 1.35;
    }

    .override-editor-shell__hint {
      color: var(--app-text-muted);
    }

    .override-editor-shell__error {
      color: var(--app-status-danger);
    }

    @media (min-width: 900px) {
      .override-editor-shell__rows {
        grid-template-columns: repeat(2, minmax(0, 1fr));
        column-gap: 1.25rem;
      }

      .override-editor-shell__row {
        align-content: start;
      }
    }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PropertyOverrideEditorComponent {
  readonly editorId = input.required<string>();
  readonly properties = input<Record<string, TaskPropertyType>>({});
  readonly initialOverrides = input<Record<string, unknown>>({});
  readonly resetKey = input('');
  readonly emptyMessage = input('No input properties are available for overrides.');
  readonly stateChange = output<PropertyOverrideEditorState>();

  protected readonly form = new FormGroup({});
  protected fields: DynamicFieldDefinition[] = [];

  constructor() {
    effect(() => {
      this.resetKey();
      const fields = createTaskPropertyFieldDefinitions(this.properties(), false);
      const initialOverrides = untracked(() => this.initialOverrides());
      this.rebuildForm(fields, initialOverrides);
    });

    this.form.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.emitState());
  }

  protected kindLabel(field: DynamicFieldDefinition): string {
    return taskPropertyKindLabel(field.kind);
  }

  protected inputId(field: DynamicFieldDefinition): string {
    return `${this.editorId()}-${field.key}`;
  }

  protected inputType(field: DynamicFieldDefinition): 'text' | 'number' {
    return field.kind === 'number' ? 'number' : 'text';
  }

  protected controlInvalid(fieldKey: string): boolean {
    const control = this.form.get(fieldKey);
    return !!control && control.invalid && (control.touched || control.dirty);
  }

  protected errorMessage(field: DynamicFieldDefinition): string {
    const control = this.form.get(field.key);
    if (!control?.errors) {
      return '';
    }

    if (control.errors['pattern']) {
      return `${field.label} must be a valid GUID.`;
    }

    return `${field.label} is invalid.`;
  }

  private rebuildForm(
    fields: DynamicFieldDefinition[],
    initialOverrides: Record<string, unknown>,
  ): void {
    this.fields = fields;

    for (const controlName of Object.keys(this.form.controls)) {
      this.form.removeControl(controlName);
    }

    for (const field of fields) {
      this.form.addControl(field.key, this.createOverrideControl(field, initialOverrides[field.key]));
    }

    this.emitState();
  }

  private createOverrideControl(
    field: DynamicFieldDefinition,
    initialValue: unknown,
  ): FormControl {
    if (field.kind === 'boolean') {
      return new FormControl(this.normalizeBooleanOverrideValue(initialValue), { nonNullable: true });
    }

    return createTaskPropertyControl(field, initialValue) as FormControl;
  }

  private emitState(): void {
    this.stateChange.emit({
      editorId: this.editorId(),
      overrides: this.serializeOverrides(),
      valid: !this.form.invalid,
    });
  }

  private serializeOverrides(): Record<string, unknown> {
    const overrides: Record<string, unknown> = {};
    for (const field of this.fields) {
      const control = this.form.get(field.key);
      if (!control) {
        continue;
      }

      const serializedValue = this.serializeOverrideValue(field, control.value);
      if (serializedValue === undefined) {
        continue;
      }

      overrides[field.key] = serializedValue;
    }

    return overrides;
  }

  private serializeOverrideValue(
    field: DynamicFieldDefinition,
    value: unknown,
  ): unknown {
    switch (field.kind) {
      case 'boolean':
        if (value !== 'true' && value !== 'false') {
          return undefined;
        }

        return value === 'true';
      case 'number':
        return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
      default: {
        const normalized = typeof value === 'string' ? value.trim() : '';
        return normalized.length > 0 ? normalized : undefined;
      }
    }
  }

  private normalizeBooleanOverrideValue(value: unknown): string {
    if (value === true) {
      return 'true';
    }

    if (value === false) {
      return 'false';
    }

    return '';
  }
}
