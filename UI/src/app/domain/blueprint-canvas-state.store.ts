import { Injectable, computed, signal } from '@angular/core';

interface BlueprintCanvasViewState {
  readonly graphRevision: number;
  readonly expandedNodeSections: Readonly<Record<number, readonly string[]>>;
}

@Injectable()
export class BlueprintCanvasStateStore {
  private readonly _state = signal<BlueprintCanvasViewState>(this.freshState(0));

  readonly expandedNodeSections = computed(() => this._state().expandedNodeSections);

  resetForGraphRevision(graphRevision: number): void {
    if (this._state().graphRevision === graphRevision) {
      return;
    }

    this._state.set(this.freshState(graphRevision));
  }

  toggleNodeSectionExpanded(nodeId: number, sectionKey: string): void {
    this._state.update((state) => {
      const currentNodeSections = new Set(state.expandedNodeSections[nodeId] ?? []);
      if (currentNodeSections.has(sectionKey)) {
        currentNodeSections.delete(sectionKey);
      } else {
        currentNodeSections.add(sectionKey);
      }

      const nextSections = { ...state.expandedNodeSections };
      if (currentNodeSections.size === 0) {
        delete nextSections[nodeId];
      } else {
        nextSections[nodeId] = [...currentNodeSections].sort((left, right) => left.localeCompare(right));
      }

      return {
        ...state,
        expandedNodeSections: nextSections,
      };
    });
  }

  ensureNodeSectionExpanded(nodeId: number, sectionKey: string): void {
    this._state.update((state) => {
      const currentNodeSections = new Set(state.expandedNodeSections[nodeId] ?? []);
      if (currentNodeSections.has(sectionKey)) {
        return state;
      }

      currentNodeSections.add(sectionKey);
      return {
        ...state,
        expandedNodeSections: {
          ...state.expandedNodeSections,
          [nodeId]: [...currentNodeSections].sort((left, right) => left.localeCompare(right)),
        },
      };
    });
  }

  private freshState(graphRevision: number): BlueprintCanvasViewState {
    return {
      graphRevision,
      expandedNodeSections: {},
    };
  }
}
