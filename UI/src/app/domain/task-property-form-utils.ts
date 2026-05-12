import { AbstractControl, FormControl, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import type { DynamicFieldDefinition } from '../components/dynamic-form-field.component';
import { TaskPropertyType } from './node-model';
import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

const GUID_PATTERN = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$/;
const INTEGER_PATTERN = /^[+-]?\d+$/;
const DECIMAL_PATTERN = /^[+-]?(?:\d+|\d*\.\d+)$/;
const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;
const DATETIME_LOCAL_PATTERN = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/;

export function createTaskPropertyFieldDefinitions(
  properties: readonly ITaskPropertyDefinition[] | Record<string, TaskPropertyType> | null | undefined,
  required = true,
): DynamicFieldDefinition[] {
  return normalizeTaskPropertyDefinitions(properties)
    .sort((left, right) => left.name.localeCompare(right.name))
    .map((property) => ({
      key: property.name,
      label: formatTaskPropertyLabel(property.name),
      kind: toTaskPropertyFieldKind(property.kind),
      required: required && property.required,
      options: scalarTaskPropertyKind(property.kind) === 'Enum' ? [...property.enumValues] : undefined,
      propertyType: property.kind,
    }));
}

export function taskPropertyKindMap(
  properties: readonly ITaskPropertyDefinition[] | Record<string, TaskPropertyType> | null | undefined,
): Record<string, TaskPropertyType> {
  return Object.fromEntries(normalizeTaskPropertyDefinitions(properties).map((property) => [property.name, property.kind]));
}

export function normalizeTaskPropertyDefinitions(
  properties: readonly ITaskPropertyDefinition[] | Record<string, TaskPropertyType> | null | undefined,
): ITaskPropertyDefinition[] {
  if (!properties) {
    return [];
  }

  if (Array.isArray(properties)) {
    return properties.map((property) => ({
      ...property,
      enumValues: [...property.enumValues],
    }));
  }

  return Object.entries(properties).map(([name, kind]) => ({
    name,
    kind,
    required: false,
    enumValues: [],
  }));
}

export function createTaskPropertyControl(
  field: DynamicFieldDefinition,
  initialValue?: unknown,
): AbstractControl {
  const validators = createTaskPropertyValidators(field);

  switch (field.kind) {
    case 'boolean':
      return new FormControl(normalizeBooleanValue(initialValue), {
        nonNullable: true,
        validators,
      });
    case 'integer':
    case 'decimal':
      return new FormControl<number | null>(
        normalizeNumberValue(initialValue),
        validators,
      );
    case 'enum':
      return new FormControl(normalizeStringValue(initialValue), {
        nonNullable: true,
        validators,
      });
    case 'array':
      return new FormControl(normalizeArrayEditorValue(initialValue), {
        nonNullable: true,
        validators,
      });
    case 'guid':
      return new FormControl(normalizeStringValue(initialValue), {
        nonNullable: true,
        validators,
      });
    case 'date':
      return new FormControl<Date | null>(
        normalizeDateControlValue(initialValue),
        validators,
      );
    case 'datetime':
      return new FormControl(normalizeDateTimeValue(initialValue), {
        nonNullable: true,
        validators,
      });
    default:
      return new FormControl(normalizeStringValue(initialValue), {
        nonNullable: true,
        validators,
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
    case 'date':
      return normalizeDateValue(value);
    case 'datetime':
      return normalizeDateTimeValue(value);
    case 'array':
      return normalizeArrayOutputValue(value);
    default:
      return normalizeStringValue(value);
  }
}

export function serializeTaskPropertyFormValues(
  fields: DynamicFieldDefinition[],
  values: Record<string, unknown>,
): Record<string, unknown> {
  return Object.fromEntries(
    fields
      .map((field) => [field.key, serializeTaskPropertyControlValue(field, values[field.key])] as const)
      .filter(([, value]) => shouldIncludeSerializedTaskPropertyValue(value)),
  );
}

export function shouldIncludeSerializedTaskPropertyValue(value: unknown): boolean {
  if (value === null || value === undefined) {
    return false;
  }

  return typeof value !== 'string' || value.trim().length > 0;
}

export function taskPropertyValidationMessage(
  field: DynamicFieldDefinition,
  errors: ValidationErrors | null | undefined,
): string {
  if (!errors) {
    return '';
  }

  if (errors['required']) {
    return `${field.label} is required.`;
  }

  if (errors['boolean']) {
    return `${field.label} must be either True or False.`;
  }

  if (errors['integer']) {
    return `${field.label} must be a whole number.`;
  }

  if (errors['decimal']) {
    return `${field.label} must be a valid decimal number.`;
  }

  if (errors['guid']) {
    return `${field.label} must be a valid GUID.`;
  }

  if (errors['date']) {
    return `${field.label} must be a real calendar date.`;
  }

  if (errors['datetime']) {
    return `${field.label} must include a real date and time.`;
  }

  if (errors['enum']) {
    return `Choose one of the available ${field.label} options.`;
  }

  if (errors['array']) {
    return `${field.label} must be a JSON array whose items match ${scalarTaskPropertyKind(field.propertyType ?? 'String')}.`;
  }

  return `${field.label} is invalid.`;
}

export function taskPropertyInputHint(field: DynamicFieldDefinition, optionalHint = ''): string {
  switch (field.kind) {
    case 'integer':
      return 'Whole numbers only.';
    case 'decimal':
      return 'Decimal number, for example 12.5.';
    case 'guid':
      return 'GUID format expected.';
    case 'date':
      return 'Choose a calendar date.';
    case 'datetime':
      return 'Choose a calendar date and local time.';
    case 'enum':
      return field.options?.length ? 'Choose one of the available options.' : optionalHint;
    case 'array':
      return 'Enter a JSON array.';
    default:
      return optionalHint;
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
    case 'array':
      return 'Collection';
    default:
      return 'String';
  }
}

function toTaskPropertyFieldKind(type: TaskPropertyType): DynamicFieldDefinition['kind'] {
  if (isCollectionTaskPropertyKind(type)) {
    return 'array';
  }

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

function createTaskPropertyValidators(field: DynamicFieldDefinition): ValidatorFn[] {
  const validators: ValidatorFn[] = field.required ? [Validators.required] : [];

  switch (field.kind) {
    case 'boolean':
      validators.push(optionalAwareValidator('boolean', isValidBooleanValue));
      break;
    case 'integer':
      validators.push(optionalAwareValidator('integer', isValidIntegerValue));
      break;
    case 'decimal':
      validators.push(optionalAwareValidator('decimal', isValidDecimalValue));
      break;
    case 'guid':
      validators.push(optionalAwareValidator('guid', isValidGuidValue));
      break;
    case 'date':
      validators.push(optionalAwareValidator('date', isValidDateValue));
      break;
    case 'datetime':
      validators.push(optionalAwareValidator('datetime', isValidDateTimeValue));
      break;
    case 'enum':
      validators.push(optionalAwareValidator('enum', (value) => isValidEnumValue(value, field.options ?? [])));
      break;
    case 'array':
      validators.push(optionalAwareValidator('array', (value) => isValidArrayValue(value, field)));
      break;
  }

  return validators;
}

function optionalAwareValidator(
  errorKey: string,
  isValid: (value: unknown) => boolean,
): ValidatorFn {
  return (control) => {
    if (isBlankValue(control.value)) {
      return null;
    }

    return isValid(control.value) ? null : { [errorKey]: true };
  };
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
  if (!isValidIntegerValue(value)) {
    return null;
  }

  return Number(value);
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

function normalizeDateValue(value: unknown): string {
  if (value instanceof Date && isValidDateObject(value)) {
    return formatDateValue(value);
  }

  const normalized = normalizeStringValue(value).trim();
  if (isValidDateText(normalized)) {
    return normalized;
  }

  const parsedDate = normalized.length ? new Date(normalized) : null;
  return parsedDate && isValidDateObject(parsedDate) ? formatDateValue(parsedDate) : normalized;
}

function normalizeDateControlValue(value: unknown): Date | null {
  if (value instanceof Date && isValidDateObject(value)) {
    return value;
  }

  const normalized = normalizeDateValue(value);
  if (!isValidDateText(normalized)) {
    return null;
  }

  const [year, month, day] = normalized.split('-').map((part) => Number(part));
  return new Date(year, month - 1, day);
}

function normalizeDateTimeValue(value: unknown): string {
  if (value instanceof Date && isValidDateObject(value)) {
    return formatDateTimeLocalValue(value);
  }

  const normalized = normalizeStringValue(value).trim();
  if (isValidDateTimeText(normalized)) {
    return normalized;
  }

  const parsedDate = normalized.length ? new Date(normalized) : null;
  return parsedDate && isValidDateObject(parsedDate) ? formatDateTimeLocalValue(parsedDate) : normalized;
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

function normalizeArrayEditorValue(value: unknown): string {
  if (Array.isArray(value)) {
    return JSON.stringify(value, null, 2);
  }

  return normalizeStringValue(value);
}

function normalizeArrayOutputValue(value: unknown): unknown[] | null {
  if (Array.isArray(value)) {
    return value;
  }

  if (typeof value !== 'string' || value.trim().length === 0) {
    return null;
  }

  try {
    const parsed = JSON.parse(value);
    return Array.isArray(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function isBlankValue(value: unknown): boolean {
  return value === null || value === undefined || (typeof value === 'string' && value.trim().length === 0);
}

function isValidBooleanValue(value: unknown): boolean {
  return value === true || value === false || value === 'true' || value === 'false';
}

function isValidIntegerValue(value: unknown): boolean {
  if (typeof value === 'number') {
    return Number.isFinite(value) && Number.isInteger(value);
  }

  return typeof value === 'string' && INTEGER_PATTERN.test(value.trim());
}

function isValidDecimalValue(value: unknown): boolean {
  if (typeof value === 'number') {
    return Number.isFinite(value);
  }

  const normalized = typeof value === 'string' ? value.trim() : '';
  return DECIMAL_PATTERN.test(normalized) && Number.isFinite(Number(normalized));
}

function isValidGuidValue(value: unknown): boolean {
  return typeof value === 'string' && GUID_PATTERN.test(value.trim());
}

function isValidEnumValue(value: unknown, options: string[]): boolean {
  if (typeof value !== 'string') {
    return false;
  }

  return options.length === 0 || options.includes(value);
}

function isValidArrayValue(value: unknown, field: DynamicFieldDefinition): boolean {
  const parsed = normalizeArrayOutputValue(value);
  if (!Array.isArray(parsed)) {
    return false;
  }

  const scalarKind = scalarTaskPropertyKind(field.propertyType ?? 'String');
  return parsed.every((item) => isValidArrayItemValue(item, scalarKind, field.options ?? []));
}

function isValidArrayItemValue(value: unknown, scalarKind: Exclude<TaskPropertyType, `${string}[]`>, options: string[]): boolean {
  switch (scalarKind) {
    case 'Integer':
      return isValidIntegerValue(value);
    case 'Decimal':
      return isValidDecimalValue(value);
    case 'Boolean':
      return isValidBooleanValue(value);
    case 'Guid':
      return isValidGuidValue(value);
    case 'Date':
      return isValidDateValue(value);
    case 'DateTime':
      return isValidDateTimeValue(value);
    case 'Enum':
      return isValidEnumValue(value, options);
    default:
      return typeof value === 'string';
  }
}

function isCollectionTaskPropertyKind(type: TaskPropertyType): type is `${Exclude<TaskPropertyType, `${string}[]`>}[]` {
  return type.endsWith('[]');
}

function scalarTaskPropertyKind(type: TaskPropertyType): Exclude<TaskPropertyType, `${string}[]`> {
  return isCollectionTaskPropertyKind(type)
    ? type.slice(0, -2) as Exclude<TaskPropertyType, `${string}[]`>
    : type;
}

function isValidDateValue(value: unknown): boolean {
  if (value instanceof Date) {
    return isValidDateObject(value);
  }

  return typeof value === 'string' && isValidDateText(value.trim());
}

function isValidDateTimeValue(value: unknown): boolean {
  if (value instanceof Date) {
    return isValidDateObject(value);
  }

  return typeof value === 'string' && isValidDateTimeText(value.trim());
}

function isValidDateText(value: string): boolean {
  const match = DATE_PATTERN.exec(value);
  if (!match) {
    return false;
  }

  return isValidDateParts(Number(match[1]), Number(match[2]), Number(match[3]));
}

function isValidDateTimeText(value: string): boolean {
  const match = DATETIME_LOCAL_PATTERN.exec(value);
  if (!match) {
    return false;
  }

  const hours = Number(match[4]);
  const minutes = Number(match[5]);
  const seconds = match[6] === undefined ? 0 : Number(match[6]);

  return isValidDateParts(Number(match[1]), Number(match[2]), Number(match[3]))
    && hours >= 0
    && hours <= 23
    && minutes >= 0
    && minutes <= 59
    && seconds >= 0
    && seconds <= 59;
}

function isValidDateParts(year: number, month: number, day: number): boolean {
  const candidate = new Date(Date.UTC(year, month - 1, day));
  return candidate.getUTCFullYear() === year
    && candidate.getUTCMonth() === month - 1
    && candidate.getUTCDate() === day;
}

function isValidDateObject(value: Date): boolean {
  return !Number.isNaN(value.getTime());
}

function formatDateValue(value: Date): string {
  return [
    value.getFullYear(),
    padDatePart(value.getMonth() + 1),
    padDatePart(value.getDate()),
  ].join('-');
}

function formatDateTimeLocalValue(value: Date): string {
  return `${formatDateValue(value)}T${padDatePart(value.getHours())}:${padDatePart(value.getMinutes())}`;
}

function padDatePart(value: number): string {
  return String(value).padStart(2, '0');
}
