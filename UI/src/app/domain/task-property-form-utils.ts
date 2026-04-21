import { AbstractControl, FormControl, Validators } from '@angular/forms';
import { DynamicFieldDefinition } from '../components/dynamic-form-field.component';
import { TaskPropertyType } from './node-model';

const GUID_PATTERN = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$/;

export function createTaskPropertyFieldDefinitions(
  properties: Record<string, TaskPropertyType> | null | undefined,
  required = true,
): DynamicFieldDefinition[] {
  if (!properties) {
    return [];
  }

  return Object.entries(properties)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, type]) => ({
      key,
      label: formatTaskPropertyLabel(key),
      kind: toTaskPropertyFieldKind(type),
      required: required && type !== 'Boolean',
    }));
}

export function createTaskPropertyControl(
  field: DynamicFieldDefinition,
  initialValue?: unknown,
): AbstractControl {
  switch (field.kind) {
    case 'boolean':
      return new FormControl(normalizeBooleanValue(initialValue), { nonNullable: true });
    case 'number':
      return new FormControl<number | null>(
        normalizeNumberValue(initialValue),
        field.required ? [Validators.required] : [],
      );
    case 'guid':
      return new FormControl(normalizeStringValue(initialValue), {
        nonNullable: true,
        validators: [
          ...(field.required ? [Validators.required] : []),
          Validators.pattern(GUID_PATTERN),
        ],
      });
    default:
      return new FormControl(normalizeStringValue(initialValue), {
        nonNullable: true,
        validators: field.required ? [Validators.required] : [],
      });
  }
}

export function serializeTaskPropertyControlValue(
  field: DynamicFieldDefinition,
  value: unknown,
): unknown {
  switch (field.kind) {
    case 'boolean':
      return normalizeBooleanValue(value);
    case 'number':
      return normalizeNumberValue(value);
    default:
      return normalizeStringValue(value);
  }
}

export function taskPropertyKindLabel(kind: DynamicFieldDefinition['kind']): string {
  switch (kind) {
    case 'boolean':
      return 'Boolean';
    case 'number':
      return 'Number';
    case 'guid':
      return 'Guid';
    default:
      return 'String';
  }
}

function toTaskPropertyFieldKind(type: TaskPropertyType): DynamicFieldDefinition['kind'] {
  switch (type) {
    case 'Number':
      return 'number';
    case 'Boolean':
      return 'boolean';
    case 'Guid':
      return 'guid';
    default:
      return 'string';
  }
}

function formatTaskPropertyLabel(key: string): string {
  return key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .replace(/^./, (value) => value.toUpperCase());
}

function normalizeBooleanValue(value: unknown): boolean {
  return value === true;
}

function normalizeNumberValue(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string' && value.trim().length > 0) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  return null;
}

function normalizeStringValue(value: unknown): string {
  if (typeof value === 'string') {
    return value;
  }

  if (value === null || value === undefined) {
    return '';
  }

  return String(value);
}
