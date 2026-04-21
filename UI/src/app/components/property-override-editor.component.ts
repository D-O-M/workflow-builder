import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDividerModule } from '@angular/material/divider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { DynamicFieldDefinition, DynamicFormFieldComponent } from './dynamic-form-field.component';
import { TaskPropertyType } from '../domain/node-model';
import {
  createTaskPropertyControl,
  createTaskPropertyFieldDefinitions,
  serializeTaskPropertyControlValue,
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
    DynamicFormFieldComponent,
    MatCardModule,
    MatChipsModule,
    MatDividerModule,
    MatSlideToggleModule,
  ],
  template: `
    <div class="override-editor-shell">
      @if (fields().length === 0) {
        <p class="override-editor-shell__empty">{{ emptyMessage() }}</p>
      } @else {
        <div class="override-editor-shell__rows">
          @for (field of fields(); track field.key) {
            <mat-card class="override-editor-shell__row" appearance="outlined">
              <div class="override-editor-shell__row-header">
                <div class="override-editor-shell__row-copy">
                  <mat-slide-toggle
                    [checked]="isEnabled(field.key)"
                    (change)="toggleField(field.key, $event.checked)"
                  >
                    {{ field.label }}
                  </mat-slide-toggle>

                  <p>
                    {{
                      isEnabled(field.key)
                        ? 'Fixed value overlays inbound data before any runtime override.'
                        : 'No fixed value is configured for this property.'
                    }}
                  </p>
                </div>

                <mat-chip-set>
                  <mat-chip>{{ kindLabel(field) }}</mat-chip>
                </mat-chip-set>
              </div>

              <mat-divider />

              @if (isEnabled(field.key)) {
                <div class="override-editor-shell__field">
                  <app-dynamic-form-field [group]="form" [field]="field" />
                </div>
              } @else {
                <p class="override-editor-shell__hint">Toggle this on to set a fixed value.</p>
              }
            </mat-card>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .override-editor-shell {
      display: flex;
      flex-direction: column;
      gap: 1rem;
    }

    .override-editor-shell__empty {
      margin: 0;
      color: var(--app-text-secondary);
    }

    .override-editor-shell__rows {
      display: grid;
      gap: 1rem;
    }

    .override-editor-shell__row {
      display: grid;
      gap: 1rem;
      padding: 1rem;
      background: rgba(255, 255, 255, 0.88);
    }

    .override-editor-shell__row-header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 1rem;
      flex-wrap: wrap;
    }

    .override-editor-shell__row-copy {
      display: grid;
      gap: 0.5rem;
    }

    .override-editor-shell__row-copy p {
      margin: 0;
      color: var(--app-text-secondary);
      font-size: 0.92rem;
    }

    .override-editor-shell__field {
      padding-top: 0.25rem;
    }

    .override-editor-shell__hint {
      margin: 0;
      color: var(--app-text-secondary);
      font-size: 0.9rem;
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
  protected readonly fields = computed<DynamicFieldDefinition[]>(() =>
    createTaskPropertyFieldDefinitions(this.properties(), true),
  );

  private readonly enabledFields = signal<Record<string, boolean>>({});

  constructor() {
    effect(() => {
      this.resetKey();
      const fields = this.fields();
      const initialOverrides = untracked(() => this.initialOverrides());
      this.rebuildForm(fields, initialOverrides);
    });

    this.form.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.emitState());
  }

  protected isEnabled(fieldKey: string): boolean {
    return this.enabledFields()[fieldKey] ?? false;
  }

  protected kindLabel(field: DynamicFieldDefinition): string {
    return taskPropertyKindLabel(field.kind);
  }

  protected toggleField(fieldKey: string, enabled: boolean): void {
    this.enabledFields.update((current) => ({
      ...current,
      [fieldKey]: enabled,
    }));

    const control = this.form.get(fieldKey);
    if (!control) {
      return;
    }

    if (enabled) {
      control.enable({ emitEvent: false });
      if (control.invalid) {
        control.markAsTouched();
      }
    } else {
      control.disable({ emitEvent: false });
      control.markAsPristine();
      control.markAsUntouched();
    }

    this.emitState();
  }

  private rebuildForm(
    fields: DynamicFieldDefinition[],
    initialOverrides: Record<string, unknown>,
  ): void {
    for (const controlName of Object.keys(this.form.controls)) {
      this.form.removeControl(controlName);
    }

    const nextEnabled: Record<string, boolean> = {};
    for (const field of fields) {
      const hasOverride = Object.prototype.hasOwnProperty.call(initialOverrides, field.key);
      const control = createTaskPropertyControl(field, initialOverrides[field.key]);
      this.form.addControl(field.key, control);

      nextEnabled[field.key] = hasOverride;
      if (!hasOverride) {
        control.disable({ emitEvent: false });
      }
    }

    this.enabledFields.set(nextEnabled);
    this.emitState();
  }

  private emitState(): void {
    this.stateChange.emit({
      editorId: this.editorId(),
      overrides: this.serializeOverrides(),
      valid: this.form.valid,
    });
  }

  private serializeOverrides(): Record<string, unknown> {
    const overrides: Record<string, unknown> = {};
    for (const field of this.fields()) {
      if (!this.isEnabled(field.key)) {
        continue;
      }

      const control = this.form.get(field.key);
      if (!control) {
        continue;
      }

      overrides[field.key] = serializeTaskPropertyControlValue(field, control.value);
    }

    return overrides;
  }
}
