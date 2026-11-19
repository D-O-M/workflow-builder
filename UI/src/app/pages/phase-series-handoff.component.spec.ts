import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import { handoffTargetLabels } from './phase-series-handoff.component';

describe('Data Bridge destination labels', () => {
  it('identifies distinct nodes sharing a task name and keeps repeat mappings to the same node concise', () => {
    const primary = 'fa123456-1234-4000-9000-000000000001';
    const secondary = 'fa123456-1234-4000-9000-000000000002';
    const labels = handoffTargetLabels([
      { targetNodeId: primary, targetNodeName: 'Score check' },
      { targetNodeId: secondary, targetNodeName: 'Score check' },
      { targetNodeId: 'audit', targetNodeName: 'Audit' },
      { targetNodeId: 'audit', targetNodeName: 'Audit' },
      { targetNodeId: 'legacy', targetNodeName: null },
    ]);
    expect(labels.get(primary)).toBe(`Score check (${primary})`);
    expect(labels.get(secondary)).toBe(`Score check (${secondary})`);
    expect(labels.get('audit')).toBe('Audit');
    expect(labels.get('legacy')).toBe('legacy');
  });
});
