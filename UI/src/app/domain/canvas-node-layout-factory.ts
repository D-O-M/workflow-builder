import type { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';
import type {
  NodeChannelSectionLayout,
  NodeKind,
  NodeLayout,
  NodePortDefinition,
  NodePropertyRow,
  TaskPropertyBaseType,
  TaskPropertyType,
} from './node-model';
import { inflateRect, sectionKey, summarySocketRect } from './canvas-node-geometry-utility';

export interface CanvasNodeLayoutTokens {
  readonly collectionIteratorOperatorWidth: number;
  readonly collectionIteratorOperatorHeight: number;
  readonly titleAreaHeight: number;
  readonly sectionHeaderHeight: number;
  readonly collapsedRowHeight: number;
  readonly propertyRowHeight: number;
  readonly sectionGap: number;
  readonly contentPaddingX: number;
  readonly bottomPadding: number;
  readonly summaryPortRadius: number;
  readonly propertyPortRadius: number;
  readonly summarySocketHitPaddingX: number;
  readonly summarySocketHitPaddingY: number;
  readonly propertyPortOffset: number;
  readonly propertyPortHitPadding: number;
  readonly toggleSize: number;
}

export const CANVAS_NODE_LAYOUT_TOKENS: CanvasNodeLayoutTokens = {
  collectionIteratorOperatorWidth: 240,
  collectionIteratorOperatorHeight: 112,
  titleAreaHeight: 60,
  sectionHeaderHeight: 30,
  collapsedRowHeight: 36,
  propertyRowHeight: 28,
  sectionGap: 8,
  contentPaddingX: 18,
  bottomPadding: 12,
  summaryPortRadius: 6.8,
  propertyPortRadius: 4.8,
  summarySocketHitPaddingX: 9,
  summarySocketHitPaddingY: 8,
  propertyPortOffset: 0,
  propertyPortHitPadding: 7,
  toggleSize: 24,
};

export interface CanvasNodeLayoutRequest {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly inputChannelName: string | null;
  readonly outputChannelNames: readonly string[];
  readonly inputProperties: Readonly<Record<string, TaskPropertyType>>;
  readonly outputChannelProperties: Readonly<Record<string, Readonly<Record<string, TaskPropertyType>>>>;
  readonly inputPropertyDefinitions: readonly ITaskPropertyDefinition[];
  readonly inputChannelDisplayName: string | null;
  readonly outputChannelDisplayNames: readonly string[];
  readonly outputChannelPropertyDefinitions: Readonly<Record<string, readonly ITaskPropertyDefinition[]>>;
  readonly nodeKind: NodeKind;
  readonly collectionIteratorItemKind: TaskPropertyBaseType | null;
}

export type CanvasNodeExpansionState = boolean | ReadonlySet<string>;

export interface CanvasNodeSectionLayoutRequest {
  readonly channelName: string;
  readonly channelLabel: string;
  readonly side: 'left' | 'right';
  readonly top: number;
  readonly expanded: boolean;
  readonly propertyDefinitions: readonly ITaskPropertyDefinition[];
  readonly anchorIndex: number;
}

export class CanvasNodeLayoutFactory {
  constructor(private readonly tokens: CanvasNodeLayoutTokens = CANVAS_NODE_LAYOUT_TOKENS) {}

  buildLayout(request: CanvasNodeLayoutRequest, expanded: CanvasNodeExpansionState): NodeLayout {
    let cursorY = request.y + this.tokens.titleAreaHeight;
    const summaryPorts: NodePortDefinition[] = [];
    const propertyPorts: NodePortDefinition[] = [];
    const toggleRect = {
      x: request.x + request.width - this.tokens.toggleSize - 14,
      y: request.y + 12,
      width: this.tokens.toggleSize,
      height: this.tokens.toggleSize,
    };

    const inputSection = request.inputChannelName
      ? this.buildSection(request, {
        channelName: request.inputChannelName,
        channelLabel: this.inputChannelLabel(request) ?? request.inputChannelName,
        side: 'left',
        top: cursorY,
        expanded: this.isSectionExpanded(expanded, 'left', request.inputChannelName),
        propertyDefinitions: request.inputPropertyDefinitions.length > 0
          ? this.clonePropertyDefinitions(request.inputPropertyDefinitions)
          : this.propertyDefinitionsFromKinds(request.inputProperties),
        anchorIndex: 0,
      })
      : null;

    if (inputSection) {
      summaryPorts.push(inputSection.summaryPort);
      propertyPorts.push(...inputSection.propertyRows.map((row) => row.port));
      cursorY = inputSection.bottom + this.tokens.sectionGap;
    }

    const outputSections = request.outputChannelNames.map((channelName, outputIndex) => {
      const section = this.buildSection(request, {
        channelName,
        channelLabel: this.outputChannelLabel(request, channelName, outputIndex),
        side: 'right',
        top: cursorY,
        expanded: this.isSectionExpanded(expanded, 'right', channelName),
        propertyDefinitions: this.outputPropertyDefinitions(request, channelName),
        anchorIndex: outputIndex + (request.inputChannelName ? 1 : 0),
      });
      summaryPorts.push(section.summaryPort);
      propertyPorts.push(...section.propertyRows.map((row) => row.port));
      cursorY = section.bottom + this.tokens.sectionGap;
      return section;
    });

    const anyExpanded = typeof expanded === 'boolean'
      ? expanded
      : expanded.size > 0;
    const totalHeight = Math.max(
      anyExpanded ? 188 : 156,
      cursorY - request.y + this.tokens.bottomPadding - this.tokens.sectionGap,
    );

    return {
      totalHeight,
      titleBaseline: request.y + 29,
      versionBaseline: request.y + 48,
      titleMaxWidth: request.width - 86,
      toggleRect,
      inputSection,
      outputSections,
      summaryPorts,
      propertyPorts,
    };
  }

  buildCollectionIteratorOperatorLayout(request: CanvasNodeLayoutRequest): NodeLayout {
    const totalHeight = this.tokens.collectionIteratorOperatorHeight;
    const centerY = request.y + totalHeight / 2;
    const operatorHeaderHeight = 40;
    const sectionTop = centerY - operatorHeaderHeight / 2;
    const toggleRect = {
      x: request.x + request.width - this.tokens.toggleSize - 12,
      y: request.y + 10,
      width: this.tokens.toggleSize,
      height: this.tokens.toggleSize,
    };
    const inputSection = this.buildOperatorSection(
      request,
      'Collection',
      'Collection',
      'left',
      sectionTop,
      operatorHeaderHeight,
      0,
      this.collectionIteratorInputDisplayKind(request),
    );
    const outputSection = this.buildOperatorSection(
      request,
      'Item',
      'Item',
      'right',
      sectionTop,
      operatorHeaderHeight,
      1,
      this.collectionIteratorItemDisplayKind(request),
    );

    return {
      totalHeight,
      titleBaseline: request.y + 78,
      versionBaseline: request.y + 94,
      titleMaxWidth: request.width - 44,
      toggleRect,
      inputSection,
      outputSections: [outputSection],
      summaryPorts: [inputSection.summaryPort, outputSection.summaryPort],
      propertyPorts: [],
    };
  }

  allSectionKeys(request: CanvasNodeLayoutRequest): string[] {
    if (request.nodeKind === 'CollectionIterator') {
      return [];
    }

    return [
      ...(request.inputChannelName ? [sectionKey('left', request.inputChannelName)] : []),
      ...request.outputChannelNames.map((channelName) => sectionKey('right', channelName)),
    ];
  }

  outputPropertyDefinitions(request: CanvasNodeLayoutRequest, channelName: string): ITaskPropertyDefinition[] {
    const explicitDefinitions = request.outputChannelPropertyDefinitions[channelName];
    if (explicitDefinitions && explicitDefinitions.length > 0) {
      return this.clonePropertyDefinitions(explicitDefinitions);
    }

    return this.propertyDefinitionsFromKinds(request.outputChannelProperties[channelName] ?? {});
  }

  outputChannelLabel(request: CanvasNodeLayoutRequest, channelName: string, outputIndex: number): string {
    return this.channelLabel(channelName, request.outputChannelDisplayNames[outputIndex] ?? null) ?? channelName;
  }

  propertyDefinitionsFromKinds(
    properties: Readonly<Record<string, TaskPropertyType>>,
  ): ITaskPropertyDefinition[] {
    return Object.entries(properties)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([name, kind]) => ({
        name,
        kind,
        required: false,
        enumValues: [],
      }));
  }

  clonePropertyDefinitions(properties: readonly ITaskPropertyDefinition[]): ITaskPropertyDefinition[] {
    return properties.map((property) => ({
      ...property,
      enumValues: [...property.enumValues],
    }));
  }

  inputChannelLabel(request: CanvasNodeLayoutRequest): string | null {
    return this.channelLabel(request.inputChannelName, request.inputChannelDisplayName);
  }

  propertyCountLabel(propertyCount: number): string {
    return propertyCount === 1 ? '1 property' : `${propertyCount} properties`;
  }

  private buildOperatorSection(
    request: CanvasNodeLayoutRequest,
    channelName: string,
    channelLabel: string,
    side: 'left' | 'right',
    top: number,
    headerHeight: number,
    anchorIndex: number,
    countLabel: string,
  ): NodeChannelSectionLayout {
    const centerY = top + headerHeight / 2;
    const summarySocketSize = this.summarySocketSize();
    const summarySocket = summarySocketRect(request.x, request.width, side, centerY, summarySocketSize);
    const summaryPortX = side === 'left'
      ? request.x
      : request.x + request.width;
    const summaryPort: NodePortDefinition = {
      key: `${side}:${channelName}:summary`,
      kind: side === 'left' ? 'input-summary' : 'output-summary',
      anchorIndex,
      channelName,
      channelLabel,
      propertyName: null,
      label: channelLabel,
      side,
      x: summaryPortX,
      y: centerY,
      radius: this.tokens.summaryPortRadius,
      visualBounds: summarySocket,
      hitBounds: inflateRect(
        summarySocket,
        this.tokens.summarySocketHitPaddingX,
        this.tokens.summarySocketHitPaddingY,
      ),
    };

    return {
      key: sectionKey(side, channelName),
      name: channelName,
      label: channelLabel,
      countLabel,
      side,
      expanded: false,
      top,
      headerTop: top,
      headerHeight,
      toggleRect: {
        x: request.x + request.width - this.tokens.toggleSize - 18,
        y: top + (headerHeight - this.tokens.toggleSize) / 2,
        width: this.tokens.toggleSize,
        height: this.tokens.toggleSize,
      },
      bottom: top + headerHeight,
      summaryPort,
      propertyRows: [],
    };
  }

  private buildSection(
    request: CanvasNodeLayoutRequest,
    section: CanvasNodeSectionLayoutRequest,
  ): NodeChannelSectionLayout {
    const normalizedProperties = section.propertyDefinitions
      .map((property) => ({
        ...property,
        enumValues: [...property.enumValues],
      }))
      .sort((left, right) => left.name.localeCompare(right.name));
    const headerHeight = section.expanded ? this.tokens.sectionHeaderHeight : this.tokens.collapsedRowHeight;
    const summaryY = section.top + headerHeight / 2;
    const key = sectionKey(section.side, section.channelName);
    const summarySocketSize = this.summarySocketSize();
    const summarySocket = summarySocketRect(request.x, request.width, section.side, summaryY, summarySocketSize);
    const summaryPortX = section.side === 'left'
      ? request.x
      : request.x + request.width;
    const toggleRect = {
      x: request.x + request.width - this.tokens.toggleSize - 18,
      y: section.top + (headerHeight - this.tokens.toggleSize) / 2,
      width: this.tokens.toggleSize,
      height: this.tokens.toggleSize,
    };
    const summaryPort: NodePortDefinition = {
      key: `${section.side}:${section.channelName}:summary`,
      kind: section.side === 'left' ? 'input-summary' : 'output-summary',
      anchorIndex: section.anchorIndex,
      channelName: section.channelName,
      channelLabel: section.channelLabel,
      propertyName: null,
      label: section.channelLabel,
      side: section.side,
      x: summaryPortX,
      y: summaryY,
      radius: this.tokens.summaryPortRadius,
      visualBounds: summarySocket,
      hitBounds: inflateRect(
        summarySocket,
        this.tokens.summarySocketHitPaddingX,
        this.tokens.summarySocketHitPaddingY,
      ),
    };

    if (!section.expanded) {
      return {
        key,
        name: section.channelName,
        label: section.channelLabel,
        countLabel: this.propertyCountLabel(normalizedProperties.length),
        side: section.side,
        expanded: section.expanded,
        top: section.top,
        headerTop: section.top,
        headerHeight,
        toggleRect,
        bottom: section.top + headerHeight,
        summaryPort,
        propertyRows: [],
      };
    }

    const propertyRows: NodePropertyRow[] = [];
    let cursorY = section.top + headerHeight;
    for (const property of normalizedProperties) {
      const centerY = cursorY + this.tokens.propertyRowHeight / 2;
      const portX = section.side === 'left'
        ? request.x - this.tokens.propertyPortOffset
        : request.x + request.width + this.tokens.propertyPortOffset;
      const port: NodePortDefinition = {
        key: `${section.side}:${section.channelName}:${property.name}`,
        kind: section.side === 'left' ? 'input-property' : 'output-property',
        anchorIndex: section.anchorIndex,
        channelName: section.channelName,
        channelLabel: section.channelLabel,
        propertyName: property.name,
        label: property.name,
        side: section.side,
        x: portX,
        y: centerY,
        radius: this.tokens.propertyPortRadius,
        hitBounds: {
          x: portX - this.tokens.propertyPortRadius - this.tokens.propertyPortHitPadding,
          y: centerY - this.tokens.propertyPortRadius - this.tokens.propertyPortHitPadding,
          width: (this.tokens.propertyPortRadius + this.tokens.propertyPortHitPadding) * 2,
          height: (this.tokens.propertyPortRadius + this.tokens.propertyPortHitPadding) * 2,
        },
      };

      propertyRows.push({
        key: port.key,
        name: property.name,
        label: property.name,
        kind: property.kind,
        displayKind: this.propertyDisplayKind(request, section.side, section.channelName, property),
        required: property.required,
        top: cursorY,
        height: this.tokens.propertyRowHeight,
        centerY,
        port,
      });
      cursorY += this.tokens.propertyRowHeight;
    }

    return {
      key,
      name: section.channelName,
      label: section.channelLabel,
      countLabel: this.propertyCountLabel(normalizedProperties.length),
      side: section.side,
      expanded: section.expanded,
      top: section.top,
      headerTop: section.top,
      headerHeight,
      toggleRect,
      bottom: cursorY,
      summaryPort,
      propertyRows,
    };
  }

  private isSectionExpanded(expanded: CanvasNodeExpansionState, side: 'left' | 'right', channelName: string): boolean {
    return typeof expanded === 'boolean'
      ? expanded
      : expanded.has(sectionKey(side, channelName));
  }

  private summarySocketSize(): number {
    return this.tokens.summaryPortRadius * 2;
  }

  private propertyDisplayKind(
    request: CanvasNodeLayoutRequest,
    side: 'left' | 'right',
    channelName: string,
    property: ITaskPropertyDefinition,
  ): string {
    if (request.nodeKind !== 'CollectionIterator' || request.collectionIteratorItemKind !== null) {
      return property.kind;
    }

    if (side === 'left' && channelName === 'Collection' && property.name === 'items') {
      return 'T[]';
    }

    if (side === 'right' && channelName === 'Item' && property.name === 'item') {
      return 'T';
    }

    return property.kind;
  }

  private collectionIteratorInputDisplayKind(request: CanvasNodeLayoutRequest): string {
    return request.nodeKind === 'CollectionIterator' && request.collectionIteratorItemKind
      ? `${request.collectionIteratorItemKind}[]`
      : 'T[]';
  }

  private collectionIteratorItemDisplayKind(request: CanvasNodeLayoutRequest): string {
    return request.nodeKind === 'CollectionIterator' && request.collectionIteratorItemKind
      ? request.collectionIteratorItemKind
      : 'T';
  }

  private channelLabel(name: string | null, displayName: string | null | undefined): string | null {
    const trimmedDisplayName = displayName?.trim();
    return trimmedDisplayName && trimmedDisplayName.length > 0
      ? trimmedDisplayName
      : name;
  }
}
