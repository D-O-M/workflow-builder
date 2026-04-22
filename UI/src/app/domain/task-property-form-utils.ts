import { AbstractControl, FormControl, Validators } from '@angular/forms';
import { DynamicFieldDefinition } from '../components/dynamic-form-field.component';
import { TaskPropertyType } from './node-model';
import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

const GUID_PATTERN = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$/;

export function createTaskPropertyFieldDefinitions(
  properties: ITaskPropertyDefinition[] | null | undefined,
  required = true,
): DynamicFieldDefinition[] {
  if (!properties) {
    return [];
  }

  return [...properties]
    .sort((left, right) => left.name.localeCompare(right.name))
    .map((property) => ({
      key: property.name,
      label: formatTaskPropertyLabel(property.name),
      kind: toTaskPropertyFieldKind(property.kind),
      required: required && property.required,
      options: property.kind === 'Enum' ? [...property.enumValues] : undefined,
    }));
}

export function taskPropertyKindMap(
  properties: ITaskPropertyDefinition[] | null | undefined,
): Record<string, TaskPropertyType> {
  if (!properties) {
    return {};
  }

  return Object.fromEntries(properties.map((property) => [property.name, property.kind]));
}

export function createTaskPropertyControl(
  field: DynamicFieldDefinition,
  initialValue?: unknown,
): AbstractControl {
  switch (field.kind) {
    case 'boolean':
      return new FormControl(normalizeBooleanValue(initialValue), {
        nonNullable: true,
        validators: field.required ? [Validators.required] : [],
      });
    case 'integer':
    case 'decimal':
      return new FormControl<number | null>(
        normalizeNumberValue(initialValue),
        field.required ? [Validators.required] : [],
      );
    case 'enum':
      return new FormControl(normalizeStringValue(initialValue), {
        nonNullable: true,
        validators: field.required ? [Validators.required] : [],
      });
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
      return normalizeBooleanOutputValue(value);
    case 'integer':
      return normalizeIntegerValue(value);
    case 'decimal':
      return normalizeNumberValue(value);
    default:
      return normalizeStringValue(value);
  }
}

export function taskPropertyKindLabel(kind: DynamicFieldDefinition['kind']): string {
  switch (kind) {
    case 'boolean':
      return 'Boolean';
    case 'integer':
      return 'Integer';
    case 'decimal':
      return 'Decimal';
    case 'guid':
      return 'Guid';
    case 'date':
      return 'Date';
    case 'datetime':
      return 'Date & time';
    case 'enum':
      return 'Option list';
    default:
      return 'String';
  }
}

function toTaskPropertyFieldKind(type: TaskPropertyType): DynamicFieldDefinition['kind'] {
  switch (type) {
    case 'Integer':
      return 'integer';
    case 'Decimal':
      return 'decimal';
    case 'Boolean':
      return 'boolean';
    case 'Guid':
      return 'guid';
    case 'Date':
      return 'date';
    case 'DateTime':
      return 'datetime';
    case 'Enum':
      return 'enum';
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

function normalizeBooleanValue(value: unknown): string {
  if (value === true || value === 'true') {
    return 'true';
  }

  if (value === false || value === 'false') {
    return 'false';
  }

  return '';
}

function normalizeBooleanOutputValue(value: unknown): boolean | null {
  if (value === 'true' || value === true) {
    return true;
  }

  if (value === 'false' || value === false) {
    return false;
  }

  return null;
}

function normalizeIntegerValue(value: unknown): number | null {
  const normalized = normalizeNumberValue(value);
  return normalized === null ? null : Math.trunc(normalized);
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
