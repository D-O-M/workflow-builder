import type { NodeLayout } from './node-model';
import {
  CanvasNodeLayoutFactory,
  CanvasNodeLayoutRequest,
} from './canvas-node-layout-factory';

export interface CanvasNodeLayoutCacheEntry {
  readonly key: string;
  readonly layout: NodeLayout;
}

export class CanvasNodeLayoutCache {
  private collapsedLayout: NodeLayout | null = null;
  private expandedLayout: NodeLayout | null = null;
  private readonly sectionLayouts = new Map<string, NodeLayout>();

  constructor(private readonly factory = new CanvasNodeLayoutFactory()) {}

  layoutForState(request: CanvasNodeLayoutRequest, expanded: boolean): NodeLayout {
    if (request.nodeKind === 'CollectionIterator') {
      this.collapsedLayout ??= this.factory.buildCollectionIteratorOperatorLayout(request);
      return this.collapsedLayout;
    }

    if (request.nodeKind === 'DataBridge') {
      this.expandedLayout ??= this.factory.buildLayout(request, true);
      return this.expandedLayout;
    }

    if (expanded) {
      this.expandedLayout ??= this.factory.buildLayout(request, true);
      return this.expandedLayout;
    }

    this.collapsedLayout ??= this.factory.buildLayout(request, false);
    return this.collapsedLayout;
  }

  layoutForExpandedSections(request: CanvasNodeLayoutRequest, expandedSectionKeys: ReadonlySet<string>): NodeLayout {
    if (request.nodeKind === 'CollectionIterator' || request.nodeKind === 'DataBridge') {
      return this.layoutForState(request, false);
    }

    if (expandedSectionKeys.size === 0) {
      return this.layoutForState(request, false);
    }

    const allSectionKeys = this.factory.allSectionKeys(request);
    if (expandedSectionKeys.size === allSectionKeys.length && allSectionKeys.every((key) => expandedSectionKeys.has(key))) {
      return this.layoutForState(request, true);
    }

    const cacheKey = this.expandedSectionCacheKey(expandedSectionKeys);
    const cachedLayout = this.sectionLayouts.get(cacheKey);
    if (cachedLayout) {
      return cachedLayout;
    }

    const layout = this.factory.buildLayout(request, expandedSectionKeys);
    this.sectionLayouts.set(cacheKey, layout);
    return layout;
  }

  private expandedSectionCacheKey(expandedSectionKeys: ReadonlySet<string>): string {
    return [...expandedSectionKeys].sort((left, right) => left.localeCompare(right)).join('|');
  }
}
