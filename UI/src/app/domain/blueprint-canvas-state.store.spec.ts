import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import { BlueprintCanvasStateStore } from './blueprint-canvas-state.store';

describe('BlueprintCanvasStateStore', () => {
  it('resets expanded sections by replacing state when the graph revision changes', () => {
    const store = new BlueprintCanvasStateStore();

    store.toggleNodeSectionExpanded(1, 'left:Input');
    store.toggleNodeSectionExpanded(1, 'right:Success');

    expect(store.expandedNodeSections()).toEqual({
      1: ['left:Input', 'right:Success'],
    });

    store.resetForGraphRevision(1);

    expect(store.expandedNodeSections()).toEqual({});
  });

  it('does not reset expanded sections when the graph revision is unchanged', () => {
    const store = new BlueprintCanvasStateStore();

    store.toggleNodeSectionExpanded(1, 'right:Success');
    store.resetForGraphRevision(0);

    expect(store.expandedNodeSections()).toEqual({
      1: ['right:Success'],
    });
  });

  it('ensures a node section is expanded without toggling it closed', () => {
    const store = new BlueprintCanvasStateStore();

    store.ensureNodeSectionExpanded(1, 'left:Input');
    store.ensureNodeSectionExpanded(1, 'left:Input');
    store.ensureNodeSectionExpanded(1, 'right:Success');

    expect(store.expandedNodeSections()).toEqual({
      1: ['left:Input', 'right:Success'],
    });
  });
});
