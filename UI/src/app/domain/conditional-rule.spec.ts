import { describe, expect, it } from 'vitest';
import type { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';
import {
  compatibleConditionalFields, ConditionalComparison, ConditionalRule, conditionalOperators,
  conditionalRuleIssues, markChangedConditionalFields, summarizeConditionalRule,
} from './conditional-rule';

const field = (name: string, kind: ITaskPropertyDefinition['kind'], enumValues: string[] = []): ITaskPropertyDefinition =>
  ({ name, kind, enumValues, required: false });
const schema = [
  field('Country', 'String'), field('Amount', 'Decimal'), field('Threshold', 'Integer'),
  field('Tier', 'Enum', ['Gold', 'Silver']), field('PreviousTier', 'Enum', ['Gold', 'Silver']),
  field('Status', 'Enum', ['Open']), field('Tags', 'String[]'), field('When', 'DateTime'),
];
const rule: ConditionalRule = {
  kind: 'all', children: [
    { kind: 'any', children: [
      { kind: 'comparison', field: 'Country', operator: 'startsWith', operands: [{ literal: 'GB' }], ignoreCase: true },
      { kind: 'comparison', field: 'Tier', operator: 'eq', operands: [{ literal: 'Gold' }] },
    ] },
    { kind: 'comparison', field: 'Amount', operator: 'lt', negated: true, operands: [{ field: 'Threshold' }] },
  ],
};

describe('Conditional Branch authoring', () => {
  it('allows nested compound rules and summarizes their grouping without exposing literals', () => {
    expect(conditionalRuleIssues(rule, schema)).toEqual([]);
    const summary = summarizeConditionalRule(rule);
    expect(summary).toContain(' OR ');
    expect(summary).toContain('(ignore case)');
    expect(summary).toContain('AND NOT Amount Less than Threshold');
    expect(summary).not.toMatch(/Gold|GB/);
  });

  it('uses each property schema to restrict operators and field operands', () => {
    expect(conditionalOperators('String').map(op => op.id)).toContain('startsWith');
    expect(conditionalOperators('Integer').map(op => op.id)).not.toContain('contains');
    const eq = conditionalOperators('Enum').find(op => op.id === 'eq')!;
    expect(compatibleConditionalFields(eq, schema[3], schema).map(f => f.name)).toEqual(['Tier', 'PreviousTier']);
    const contains = conditionalOperators('String[]').find(op => op.id === 'contains')!;
    expect(compatibleConditionalFields(contains, schema[6], schema).map(f => f.name)).toEqual(['Country']);
    const containsAny = conditionalOperators('String[]').find(op => op.id === 'containsAny')!;
    expect(compatibleConditionalFields(containsAny, schema[6], schema).map(f => f.name)).toEqual(['Country', 'Tags']);
    expect(conditionalRuleIssues({
      kind: 'comparison', field: 'Tags', operator: 'containsAny', operands: [{ field: 'Tags' }, { literal: 'GB' }],
    }, schema)).toEqual([]);
  });

  it('requires review after an incompatible field change and preserves the rule until repaired', () => {
    const original: ConditionalComparison = { kind: 'comparison', field: 'Amount', operator: 'eq', operands: [{ literal: '150' }] };
    const retyped = [field('Amount', 'String')];
    const affected = markChangedConditionalFields(original, schema, retyped);
    expect(affected).toMatchObject({ ...original, reviewTypes: { Amount: 'Decimal' } });
    expect(conditionalRuleIssues(affected, retyped)).toContainEqual(expect.stringContaining('Confirm'));
    expect(conditionalRuleIssues(markChangedConditionalFields(affected, retyped, schema), schema)).toEqual([]);
    expect(conditionalRuleIssues(original, [])).toContainEqual(expect.stringContaining('Amount is unavailable'));
    expect(conditionalRuleIssues(markChangedConditionalFields(original, schema, [field('Amount', 'Integer')]), [field('Amount', 'Integer')])).toEqual([]);
  });

  it('validates literal kinds, arity and case settings without numeric coercion', () => {
    const base: ConditionalComparison = { kind: 'comparison', field: 'Amount', operator: 'between',
      operands: [{ literal: '9007199254740993' }, { literal: '9007199254740994.1' }] };
    expect(conditionalRuleIssues(base, schema)).toEqual([]);
    expect(summarizeConditionalRule(base, true, schema)).toBe('Amount Between (inclusive) 9007199254740993, 9007199254740994.1');
    expect(conditionalRuleIssues({ ...base, operands: [{ literal: 'GB' }] }, schema)).toHaveLength(2);
    expect(conditionalRuleIssues({ ...base, ignoreCase: true }, schema)).toHaveLength(1);
    expect(conditionalRuleIssues({ ...base, field: 'Tier', operator: 'eq', operands: [{ literal: 'Platinum' }] }, schema)).toHaveLength(1);
    for (const invalid of ['2026-11-06', '2026-02-30T12:00', '2026-11-06T25:00Z', '2026-11-06T20:00+14:30']) {
      expect(conditionalRuleIssues({ ...base, field: 'When', operator: 'eq', operands: [{ literal: invalid }] }, schema)).toHaveLength(1);
    }
    expect(conditionalRuleIssues({ ...base, field: 'When', operator: 'eq', operands: [{ literal: '2024-02-29T23:15:00.1234567+05:30' }] }, schema)).toEqual([]);
    expect(conditionalRuleIssues({ ...base, operator: 'eq', operands: [{ field: null, literal: '1.25' }] }, schema)).toEqual([]);
    expect(conditionalRuleIssues({ ...base, operator: 'eq', operands: [{ field: 'Threshold', literal: null }] }, schema)).toEqual([]);
    expect(conditionalRuleIssues({ ...base, operator: 'eq', operands: [{ literal: '1e1025' }] }, schema)).toHaveLength(1);
    expect(conditionalRuleIssues({ ...base, field: 'Threshold', operator: 'eq', operands: [{ literal: '100.0e-2' }] }, schema)).toEqual([]);
  });
});
