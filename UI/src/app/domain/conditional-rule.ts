import type { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';
import type { TaskPropertyType } from './node-model';
import catalog from './conditional-operators.json';

export interface ConditionalOperand {
  field?: string | null;
  literal?: string | boolean | number | null;
}

export interface ConditionalComparison {
  kind: 'comparison';
  negated?: boolean;
  field: string;
  operator: string;
  ignoreCase?: boolean;
  operands: ConditionalOperand[];
  /** Draft-only, cleared when the author confirms a changed field type. */
  reviewTypes?: Record<string, TaskPropertyType>;
}

export interface ConditionalGroup {
  kind: 'all' | 'any';
  negated?: boolean;
  children: ConditionalRule[];
}

export type ConditionalRule = ConditionalComparison | ConditionalGroup;
export const maxConditionalDepth = 8;
export const maxConditionalNodes = 128;
export const emptyConditionalGroup = (): ConditionalGroup => ({ kind: 'all', children: [] });
export const emptyConditionalComparison = (): ConditionalComparison => ({
  kind: 'comparison', field: '', operator: '', operands: [],
});

export type ConditionalOperator = (typeof catalog)[number];

export function conditionalOperators(kind?: TaskPropertyType): ConditionalOperator[] {
  const family = kind?.endsWith('[]') ? 'Collection' : kind;
  return family ? catalog.filter(op => op.kinds.includes(family)) : [];
}

export function conditionalOperandKind(op: ConditionalOperator, field: ITaskPropertyDefinition): TaskPropertyType {
  if (op.operandKind === 'count') return 'Integer';
  if (op.operandKind === 'item') return field.kind.slice(0, -2) as TaskPropertyType;
  return field.kind;
}

function compatibleKinds(left: TaskPropertyType, right: TaskPropertyType): boolean {
  return left === right || (['Integer', 'Decimal'].includes(left) && ['Integer', 'Decimal'].includes(right));
}

export function compatibleConditionalFields(
  op: ConditionalOperator,
  left: ITaskPropertyDefinition,
  fields: readonly ITaskPropertyDefinition[],
): readonly ITaskPropertyDefinition[] {
  const kind = conditionalOperandKind(op, left);
  return fields.filter(candidate => {
    const candidateKind = op.acceptsCollectionField && candidate.kind.endsWith('[]')
      ? candidate.kind.slice(0, -2) as TaskPropertyType
      : candidate.kind;
    return compatibleKinds(kind, candidateKind) &&
      (kind !== 'Enum' || (left.enumValues.length === candidate.enumValues.length &&
        left.enumValues.every(value => candidate.enumValues.includes(value))));
  });
}

export function markChangedConditionalFields(
  rule: ConditionalRule,
  previous: readonly ITaskPropertyDefinition[],
  current: readonly ITaskPropertyDefinition[],
): ConditionalRule {
  if (rule.kind !== 'comparison') {
    return { ...rule, children: rule.children.map(child => markChangedConditionalFields(child, previous, current)) };
  }
  const review = { ...rule.reviewTypes };
  const references = [rule.field, ...rule.operands.flatMap(operand => operand.field ? [operand.field] : [])];
  for (const name of references) {
    const before = review[name] ?? previous.find(field => field.name === name)?.kind;
    const after = current.find(field => field.name === name)?.kind;
    if (!before) continue;
    if (after && compatibleKinds(before, after)) delete review[name];
    else review[name] = before;
  }
  return Object.keys(review).length ? { ...rule, reviewTypes: review } : { ...rule, reviewTypes: undefined };
}

function validNumericLiteral(text: string, integer: boolean): boolean {
  if (text.length > 1024) return false;
  const match = /^[+-]?(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/u.exec(text);
  if (!match || (!match[1] && !match[2])) return false;
  const exponent = Number(match[3] ?? 0);
  if (Math.abs(exponent) > 1024) return false;
  const fraction = match[2] ?? '';
  const scale = fraction.length - exponent;
  // Inspect trailing digits so a whole number beyond JS safe integer precision stays exact.
  return !integer || scale <= 0 || !/[1-9]/.test((match[1] + fraction).slice(-scale));
}

function validCalendarDate(text: string): boolean {
  return /^\d{4}-\d{2}-\d{2}$/.test(text) && !text.startsWith('0000') &&
    !Number.isNaN(Date.parse(text)) && new Date(text).toISOString().slice(0, 10) === text;
}

function validDateTime(text: string): boolean {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.\d{1,7})?)?(?:Z|[+-](\d{2}):(\d{2}))?$/.exec(text);
  if (!match || !validCalendarDate(match[1])) return false;
  const [, , hour, minute, second = '00', offsetHours = '00', offsetMinutes = '00'] = match;
  return Number(hour) < 24 && Number(minute) < 60 && Number(second) < 60 &&
    Number(offsetMinutes) < 60 && (Number(offsetHours) < 14 || (offsetHours === '14' && offsetMinutes === '00'));
}

function validLiteral(value: ConditionalOperand['literal'], kind: TaskPropertyType, options: readonly string[]): boolean {
  if (kind === 'Boolean') return typeof value === 'boolean';
  if (value == null || typeof value === 'boolean') return false;
  const text = String(value);
  switch (kind) {
    case 'String': return typeof value === 'string';
    case 'Integer': return validNumericLiteral(text, true);
    case 'Decimal': return validNumericLiteral(text, false);
    case 'Guid': return /^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i.test(text);
    case 'Date': return validCalendarDate(text);
    case 'DateTime': return validDateTime(text);
    case 'Enum': return options.includes(text);
    default: return false;
  }
}

export function conditionalRuleIssues(root: ConditionalRule | null, fields: readonly ITaskPropertyDefinition[]): string[] {
  const issues: string[] = [];
  let nodes = 0;
  const visit = (rule: ConditionalRule, path: string, depth: number): void => {
    nodes++;
    if (depth > maxConditionalDepth || nodes > maxConditionalNodes) return;
    const report = (message: string) => issues.push(path + ' — ' + message);
    if (rule.kind !== 'comparison') {
      if (!rule.children.length) report('Add a condition or group.');
      rule.children.forEach((child, i) => {
        if (depth === maxConditionalDepth) report('Groups can be nested up to eight levels.');
        else visit(child, path + '.' + (i + 1), depth + 1);
      });
      return;
    }

    const left = fields.find(f => f.name === rule.field);
    if (!left) {
      report(rule.field ? 'Source field ' + rule.field + ' is unavailable.' : 'Choose a source field.');
      return;
    }
    if (rule.reviewTypes && Object.keys(rule.reviewTypes).length) report('Confirm changed field types before saving.');
    const op = conditionalOperators(left.kind).find(candidate => candidate.id === rule.operator);
    if (!op) { report('Choose a comparison for ' + left.kind + '.'); return; }
    if (rule.operands.length < op.minOperands || rule.operands.length > op.maxOperands) report('Check the number of comparison values.');
    if (rule.ignoreCase && !op.ignoreCase) report('This comparison is case-sensitive.');
    for (const [i, operand] of rule.operands.entries()) {
      const valid = operand.field != null
        ? operand.literal == null && compatibleConditionalFields(op, left, fields).some(f => f.name === operand.field)
        : validLiteral(operand.literal, conditionalOperandKind(op, left), left.enumValues);
      if (!valid) report('Choose a compatible field or enter a valid value for operand ' + (i + 1) + '.');
    }
  };
  if (!root) return ['Add a condition or group.'];
  visit(root, 'Rule', 1);
  if (nodes > maxConditionalNodes) issues.push('Use at most 128 rules and groups.');
  return issues;
}

export function summarizeConditionalRule(
  rule: ConditionalRule | null | undefined,
  includeLiterals = false,
  fields: readonly ITaskPropertyDefinition[] = [],
): string {
  if (!rule) return '';
  let summary: string;
  if (rule.kind === 'comparison') {
    const left = fields.find(field => field.name === rule.field);
    const op = (left ? conditionalOperators(left.kind) : catalog).find(candidate => candidate.id === rule.operator);
    const kind = left && op ? conditionalOperandKind(op, left) : null;
    const values = rule.operands.map(operand => {
      if (operand.field != null) return operand.field;
      if (!includeLiterals) return 'value';
      return kind === 'Integer' || kind === 'Decimal' ? String(operand.literal) : JSON.stringify(operand.literal);
    });
    const label = op?.label ?? rule.operator;
    summary = [rule.field, label, values.join(', ')].filter(Boolean).join(' ');
    if (rule.ignoreCase) summary += ' (ignore case)';
  } else {
    const children = rule.children.map(child => summarizeConditionalRule(child, includeLiterals, fields));
    summary = '(' + children.join(rule.kind === 'all' ? ' AND ' : ' OR ') + ')';
  }
  return rule.negated ? 'NOT ' + summary : summary;
}
