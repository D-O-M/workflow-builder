import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import type { DynamicFieldDefinition } from '../components/dynamic-form-field.component';
import {
  createTaskPropertyControl,
  serializeTaskPropertyControlValue,
  serializeTaskPropertyFormValues,
  taskPropertyValidationMessage,
} from './task-property-form-utils';

function field(
  kind: DynamicFieldDefinition['kind'],
  overrides: Partial<DynamicFieldDefinition> = {},
): DynamicFieldDefinition {
  return {
    key: overrides.key ?? kind,
    label: overrides.label ?? kind,
    kind,
    required: overrides.required ?? false,
    options: overrides.options,
  };
}

describe('task property form utils', () => {
  it('validates and serializes boolean values without treating false as empty', () => {
    const definition = field('boolean', { required: true });
    const control = createTaskPropertyControl(definition);

    control.setValue('maybe');
    expect(control.hasError('boolean')).toBe(true);

    control.setValue('false');
    expect(control.valid).toBe(true);
    expect(serializeTaskPropertyControlValue(definition, control.value)).toBe(false);
  });

  it('requires integers to be whole numbers', () => {
    const definition = field('integer');
    const control = createTaskPropertyControl(definition);

    control.setValue('12.5');
    expect(control.hasError('integer')).toBe(true);

    control.setValue('12');
    expect(control.valid).toBe(true);
    expect(serializeTaskPropertyControlValue(definition, control.value)).toBe(12);
  });

  it('validates decimal text before serializing to JSON numbers', () => {
    const definition = field('decimal');
    const control = createTaskPropertyControl(definition);

    control.setValue('12..5');
    expect(control.hasError('decimal')).toBe(true);

    control.setValue('12.5');
    expect(control.valid).toBe(true);
    expect(serializeTaskPropertyControlValue(definition, control.value)).toBe(12.5);
  });

  it('validates real date and date-time values', () => {
    const dateDefinition = field('date');
    const dateControl = createTaskPropertyControl(dateDefinition);
    dateControl.setValue('2026-02-31');
    expect(dateControl.hasError('date')).toBe(true);
    dateControl.setValue('2024-02-29');
    expect(dateControl.valid).toBe(true);

    const dateTimeDefinition = field('datetime');
    const dateTimeControl = createTaskPropertyControl(dateTimeDefinition);
    dateTimeControl.setValue('2026-04-25');
    expect(dateTimeControl.hasError('datetime')).toBe(true);
    dateTimeControl.setValue('2026-04-25T24:00');
    expect(dateTimeControl.hasError('datetime')).toBe(true);
    dateTimeControl.setValue('2026-04-25T14:30');
    expect(dateTimeControl.valid).toBe(true);
  });

  it('serializes material datepicker dates as workflow date strings', () => {
    const definition = field('date');
    const control = createTaskPropertyControl(definition, new Date(2026, 4, 12));

    expect(serializeTaskPropertyControlValue(definition, control.value)).toBe('2026-05-12');
  });

  it('validates GUID and enum option values', () => {
    const guidDefinition = field('guid');
    const guidControl = createTaskPropertyControl(guidDefinition);
    guidControl.setValue('not-a-guid');
    expect(guidControl.hasError('guid')).toBe(true);
    guidControl.setValue('d290f1ee-6c54-4b01-90e6-d701748f0851');
    expect(guidControl.valid).toBe(true);

    const enumDefinition = field('enum', { options: ['DryRun', 'Live'] });
    const enumControl = createTaskPropertyControl(enumDefinition);
    enumControl.setValue('Shadow');
    expect(enumControl.hasError('enum')).toBe(true);
    enumControl.setValue('Live');
    expect(enumControl.valid).toBe(true);
  });

  it('omits blank optional values when serializing form groups', () => {
    expect(serializeTaskPropertyFormValues(
      [
        field('string', { key: 'note' }),
        field('integer', { key: 'count' }),
        field('boolean', { key: 'enabled' }),
      ],
      {
        note: '',
        count: null,
        enabled: 'false',
      },
    )).toEqual({
      enabled: false,
    });
  });

  it('provides property-specific validation messages', () => {
    const definition = field('decimal', { label: 'Target score' });
    const control = createTaskPropertyControl(definition);
    control.setValue('abc');

    expect(taskPropertyValidationMessage(definition, control.errors)).toBe(
      'Target score must be a valid decimal number.',
    );
  });
});
