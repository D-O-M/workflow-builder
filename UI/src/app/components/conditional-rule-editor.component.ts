import { afterNextRender, ChangeDetectionStrategy, Component, computed, ElementRef, inject, Injector, input, output, viewChild, viewChildren } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import type { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';
import {
  compatibleConditionalFields, conditionalOperandKind, conditionalOperators, conditionalRuleIssues,
  ConditionalComparison, ConditionalGroup, ConditionalOperand, ConditionalRule,
  emptyConditionalComparison, emptyConditionalGroup, maxConditionalDepth, summarizeConditionalRule,
} from '../domain/conditional-rule';

@Component({
  selector: 'app-conditional-rule-editor',
  imports: [MatButtonModule, MatIconModule],
  templateUrl: './conditional-rule-editor.component.html',
  styleUrl: './conditional-rule-editor.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConditionalRuleEditorComponent {
  readonly rule = input.required<ConditionalRule>();
  readonly fields = input.required<readonly ITaskPropertyDefinition[]>();
  readonly depth = input(1);
  readonly siblingIndex = input(0);
  readonly siblingCount = input(1);
  readonly ruleChange = output<ConditionalRule>();
  readonly remove = output<void>();
  readonly move = output<-1 | 1>();
  private readonly injector = inject(Injector);
  private readonly childEditors = viewChildren<ConditionalRuleEditorComponent>('childEditor');
  private readonly upButton = viewChild('upButton', { read: ElementRef<HTMLButtonElement> });
  private readonly downButton = viewChild('downButton', { read: ElementRef<HTMLButtonElement> });
  protected readonly maxDepth = maxConditionalDepth;
  protected readonly summary = computed(() => {
    if (this.depth() !== 1 || conditionalRuleIssues(this.rule(), this.fields()).length) return '';
    return summarizeConditionalRule(this.rule(), true, this.fields());
  });
  protected readonly comparison = computed(() => {
    const rule = this.rule();
    return rule.kind === 'comparison' ? rule : null;
  });
  protected readonly left = computed(() => this.fields().find(field => field.name === this.comparison()?.field));
  protected readonly operators = computed(() => conditionalOperators(this.left()?.kind));
  protected readonly operator = computed(() => this.operators().find(op => op.id === this.comparison()?.operator));
  protected readonly operandKind = computed(() => {
    const op = this.operator();
    const left = this.left();
    return op && left ? conditionalOperandKind(op, left) : null;
  });
  protected readonly candidates = computed(() => {
    const op = this.operator();
    const left = this.left();
    return op && left ? compatibleConditionalFields(op, left, this.fields()) : [];
  });
  protected readonly changedTypes = computed(() => Object.entries(this.comparison()?.reviewTypes ?? {})
    .map(([name, before]) => name + ' (' + before + ' → ' + (this.fields().find(f => f.name === name)?.kind ?? 'unavailable') + ')')
    .join(', '));

  protected toggleNot(): void {
    const rule = this.rule();
    this.ruleChange.emit({ ...rule, negated: !rule.negated });
  }

  protected setGroupKind(group: ConditionalGroup, value: string): void {
    this.ruleChange.emit({ ...group, kind: value === 'any' ? 'any' : 'all' });
  }

  protected wrapInGroup(comparison: ConditionalComparison): void {
    this.ruleChange.emit({ kind: 'all', children: [comparison] });
  }

  protected addChild(group: ConditionalGroup, nested: boolean): void {
    this.ruleChange.emit({ ...group, children: [...group.children, nested ? emptyConditionalGroup() : emptyConditionalComparison()] });
  }

  protected changeChild(group: ConditionalGroup, index: number, replacement?: ConditionalRule): void {
    const children = [...group.children];
    if (replacement) children[index] = replacement;
    else children.splice(index, 1);
    this.ruleChange.emit({ ...group, children });
  }

  protected moveChild(group: ConditionalGroup, index: number, direction: -1 | 1): void {
    const target = index + direction;
    if (target < 0 || target >= group.children.length) return;
    const children = [...group.children];
    [children[index], children[target]] = [children[target], children[index]];
    this.ruleChange.emit({ ...group, children });
    afterNextRender(() => this.childEditors()[target]?.focusMoveButton(direction), { injector: this.injector });
  }

  private focusMoveButton(direction: -1 | 1): void {
    const preferred = direction === -1 ? this.upButton() : this.downButton();
    const alternate = direction === -1 ? this.downButton() : this.upButton();
    (preferred?.nativeElement.disabled ? alternate : preferred)?.nativeElement.focus();
  }

  protected setField(rule: ConditionalComparison, field: string): void {
    const kind = this.fields().find(f => f.name === field)?.kind;
    const options = conditionalOperators(kind);
    const op = options.find(candidate => candidate.id === 'eq') ?? options[0];
    this.ruleChange.emit({
      kind: 'comparison', field, negated: rule.negated, operator: op?.id ?? '',
      operands: Array.from({ length: op?.minOperands ?? 0 }, () => ({})),
    });
  }

  protected setOperator(rule: ConditionalComparison, id: string): void {
    const op = this.operators().find(candidate => candidate.id === id);
    if (!op) return;
    const previous = this.operator();
    const operands = op.operandKind === previous?.operandKind ? rule.operands.slice(0, op.maxOperands) : [];
    while (operands.length < op.minOperands) operands.push({});
    this.ruleChange.emit({ ...rule, operator: id, operands, ignoreCase: op.ignoreCase && rule.ignoreCase });
  }

  protected updateOperand(rule: ConditionalComparison, index: number, operand: ConditionalOperand): void {
    const operands = [...rule.operands];
    operands[index] = operand;
    this.ruleChange.emit({ ...rule, operands });
  }

  protected setLiteral(rule: ConditionalComparison, index: number, value: string): void {
    this.updateOperand(rule, index, {
      literal: this.operandKind() === 'Boolean' ? value === 'true' : value,
    });
  }

  protected removeOperand(rule: ConditionalComparison, index: number): void {
    this.ruleChange.emit({ ...rule, operands: rule.operands.filter((_, i) => i !== index) });
  }

  protected confirmTypes(rule: ConditionalComparison): void {
    const { reviewTypes, ...confirmed } = rule;
    this.ruleChange.emit(confirmed);
  }

  protected toggleCase(rule: ConditionalComparison): void {
    this.ruleChange.emit({ ...rule, ignoreCase: !rule.ignoreCase });
  }
}
